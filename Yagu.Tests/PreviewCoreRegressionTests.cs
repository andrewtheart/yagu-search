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
    private static readonly string MainWindowSource = ReadMainWindowSources();
    private static readonly string PreviewEditorSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewEditor.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string TerminalHtml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Assets", "terminal.html"));

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
    public void LauncherMode_RetainsCardSpacingWithoutExtraWindowBottomSpace()
    {
        string searchCard = ExtractXamlWindow("<!-- Search controls card -->", 600);
        Assert.Contains("Margin=\"16,10,16,4\"", searchCard);
        Assert.Contains("Padding=\"16,12\"", searchCard);
        Assert.DoesNotContain("Margin=\"16,10,16,0\"", searchCard);
        Assert.DoesNotContain("Padding=\"16,12,16,2\"", searchCard);
        Assert.DoesNotContain("Padding=\"16,12,16,8\"", searchCard);

        string progressRow = ExtractXamlWindow("x:Name=\"SearchStatusPanel\"", 900);
        Assert.Contains("Grid.Row=\"3\"", progressRow);
        Assert.Contains("Padding=\"16,2,16,2\"", progressRow);
        Assert.Contains("Spacing=\"4\"", progressRow);
        Assert.Contains("Height=\"6\"", progressRow);
        Assert.DoesNotContain("Margin=\"0,2,0,2\"", progressRow);
        Assert.DoesNotContain("Padding=\"16,0,16,0\"", progressRow);

        string splitPane = ExtractXamlWindow("x:Name=\"SplitPaneGrid\"", 300);
        Assert.Contains("Margin=\"16,2,16,4\"", splitPane);
        Assert.DoesNotContain("Margin=\"16,2,20,4\"", splitPane);
        Assert.DoesNotContain("Margin=\"16,0,16,4\"", splitPane);

        Assert.Contains("private const double MinimumLauncherHeightDip = 190;", MainWindowSource);
        Assert.Contains("desiredHeightDip < MinimumLauncherHeightDip", MainWindowSource);
        Assert.Contains("h < MinimumLauncherHeightDip", MainWindowSource);
        Assert.DoesNotContain("desiredHeightDip < 225", MainWindowSource);
        Assert.DoesNotContain("h < 225", MainWindowSource);
    }

    [Fact]
    public void PreviewPanel_EmptyVisibleSurfaceShowsCenteredWrappedMessage()
    {
        string emptyState = ExtractXamlWindow("x:Name=\"PreviewEmptyState\"", 1200);
        Assert.Contains("Grid.Row=\"1\"", emptyState);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", emptyState);
        Assert.Contains("VerticalAlignment=\"Stretch\"", emptyState);
        Assert.Contains("Text=\"Nothing to show\"", emptyState);
        Assert.Contains("FontSize=\"24\"", emptyState);
        Assert.Contains("Text=\"Add files to this preview by selecting one or more files on the left panel, right clicking, and selecting the preview option.\"", emptyState);
        Assert.Contains("FontSize=\"13\"", emptyState);
        Assert.Contains("TextWrapping=\"WrapWholeWords\"", emptyState);

        string update = ExtractMethodWindow(MainWindowSource, "UpdatePreviewEmptyState", 1600);
        Assert.Contains("PreviewPanelBorder.Visibility == Visibility.Visible", update);
        Assert.Contains("PreviewBlock.Blocks.Count > 0", update);
        Assert.Contains("PreviewSectionsPanel.Children.OfType<Expander>().Any()", update);
        Assert.Contains("PreviewEmptyState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;", update);

        string remove = ExtractMethodWindow(MainWindowSource, "RemovePreviewSection", 2600);
        AssertContainsInOrder(remove,
            "if (!PreviewSectionsPanel.Children.OfType<Expander>().Any())",
            "PreviewToolbarContent.Visibility = Visibility.Collapsed;",
            "UpdatePreviewEmptyState();");
    }

    [Fact]
    public void ClosingPreviewSection_DeselectsAndCollapsesMatchingLeftFileGroup()
    {
        Assert.Contains("dismissBtn.Click += (_, _) => RemovePreviewSection(capturedBlock, capturedPath);", MainWindowSource);
        Assert.Contains("closeItem.Click += (_, _) => RemovePreviewSection(capturedBlock, capturedPath);", MainWindowSource);

        string remove = ExtractMethodWindow(MainWindowSource, "RemovePreviewSection", 3600);
        AssertContainsInOrder(remove,
            "FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase))",
            "group.DeselectAll();",
            "group.IsExpanded = false;",
            "group.ClearVisibleResults();");

        Assert.Contains("IsExpanded=\"{x:Bind IsExpanded, Mode=TwoWay}\"", MainWindowXaml);
        Assert.Contains("IsChecked=\"{x:Bind AllSelected, Mode=OneWay}\"", MainWindowXaml);
    }

    [Fact]
    public void PreviewTopExpandedLayout_KeepsSearchDrawerSyncedToSettledPaneWidths()
    {
        string splitPane = ExtractXamlWindow("x:Name=\"SplitPaneGrid\"", 500);
        Assert.Contains("SizeChanged=\"OnTopExpandedPreviewLayoutSourceSizeChanged\"", splitPane);

        string resultsPanel = ExtractXamlWindow("x:Name=\"ResultsPanelBorder\"", 500);
        Assert.Contains("SizeChanged=\"OnTopExpandedPreviewLayoutSourceSizeChanged\"", resultsPanel);

        string previewPanel = ExtractXamlWindow("x:Name=\"PreviewPanelBorder\"", 500);
        Assert.Contains("SizeChanged=\"OnTopExpandedPreviewLayoutSourceSizeChanged\"", previewPanel);

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyTopExpandedPreviewLayout", 1800);
        Assert.Contains("SplitPaneGrid.Margin = new Thickness(16, 10, 16, 4);", apply);
        Assert.DoesNotContain("SplitPaneGrid.Margin = new Thickness(16, 10, 20, 4);", apply);
        Assert.Contains("ListenForTopExpandedPreviewLayoutSync();", apply);

        string sync = ExtractMethodWindow(MainWindowSource, "ListenForTopExpandedPreviewLayoutSync", 2200);
        Assert.Contains("CompositionTarget.Rendering += handler;", sync);
        Assert.Contains("CompositionTarget.Rendering -= handler;", sync);
        Assert.Contains("UpdateTopExpandedPreviewMeasurements();", sync);

        string update = ExtractMethodWindow(MainWindowSource, "UpdateTopExpandedPreviewMeasurements", 2600);
        Assert.Contains("ResultsPanelBorder.ActualWidth", update);
        Assert.Contains("PreviewPanelBorder.ActualWidth", update);
        Assert.Contains("SearchControlsBorder.MaxWidth = drawerWidth;", update);
        Assert.Contains("SearchStatusPanel.MaxWidth = drawerWidth;", update);
    }

    [Fact]
    public void PreviewPanel_HidesEmptyStateBeforePreviewContentRenders()
    {
        Assert.Contains("private bool _previewContentPending;", MainWindowSource);

        string emptyStateUpdate = ExtractMethodWindow(MainWindowSource, "UpdatePreviewEmptyState", 1700);
        Assert.Contains("|| _previewContentPending", emptyStateUpdate);

        string singleSelection = ExtractMethodWindow(MainWindowSource, "UpdatePreviewAsync", 1200);
        AssertContainsInOrder(singleSelection,
            "BeginPreviewContentUpdate();",
            "EnsurePreviewPanelVisible();",
            "await ShowSingleFilePreviewAsync(r, fullFile: false);");

        string singlePreview = ExtractMethodWindow(MainWindowSource, "ShowSingleFilePreviewAsync", 2300);
        AssertContainsInOrder(singlePreview,
            "BeginPreviewContentUpdate();",
            "ShowPreviewBlockSurface();",
            "PreviewBlock.Blocks.Clear();");
        Assert.Contains("CompletePreviewContentUpdate();", singlePreview);

        string multiSelection = ExtractMethodWindow(MainWindowSource, "UpdateMultiSelectPreviewAsync", 2600);
        AssertContainsInOrder(multiSelection,
            "var selected = ViewModel.GetAllSelectedResults();",
            "BeginPreviewContentUpdate();",
            "EnsurePreviewPanelVisible();");

        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync", 5200);
        AssertContainsInOrder(prepend,
            "BeginPreviewContentUpdate();",
            "EnsurePreviewPanelVisible();",
            "EnsureSectionsSurface();");
        AssertContainsInOrder(prepend,
            "PreviewSectionsPanel.Children.Insert(insertIndex++, built[i]);",
            "if (i == 0)",
            "CompletePreviewContentUpdate();");

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", 6500);
        AssertContainsInOrder(addSection,
            "PreviewSectionsPanel.Children.Add(expander);",
            "CompletePreviewContentUpdate();");
    }

    [Fact]
    public void MatchLineCheckbox_RebuildsMultiSelectPreviewForAdditionalCheckedMatches()
    {
        string handler = ExtractMethodWindow(MainWindowSource, "OnMatchLineCheckBoxClicked", 2400);

        AssertContainsInOrder(handler,
            "if (result.IsSelected)",
            "var selected = ViewModel.GetAllSelectedResults();",
            "if (selected.Count > 1)",
            "await UpdateMultiSelectPreviewAsync(result);",
            "else",
            "await EnsureCheckedMatchInPreviewAsync(result);");

        string updateMulti = ExtractMethodWindow(MainWindowSource, "UpdateMultiSelectPreviewAsync", 900);
        Assert.Contains("SearchResult? scrollTarget", updateMulti);
    }

    [Fact]
    public void PreviewAndEditorContextMenus_OpenDisplaySettingsWithSurfaceSpecificLabels()
    {
        string previewFlyout = ExtractMethodWindow(MainWindowSource, "AttachPreviewBlockContextFlyout", 3400);
        AssertContainsInOrder(previewFlyout,
            "Text = \"Change preview fonts/colors...\"",
            "OpenSettingsTab(SettingsDisplayTabIndex)",
            "flyout.Items.Add(displaySettingsItem);");

        string editorFlyout = ExtractMethodWindow(PreviewEditorSource, "InitializePreviewEditorZoom", 2600);
        AssertContainsInOrder(editorFlyout,
            "Text = \"Change editor font/colors...\"",
            "OpenSettingsTab(SettingsDisplayTabIndex)",
            "flyout.Items.Add(displaySettingsItem);");
        Assert.DoesNotContain("Change preview fonts/colors...", editorFlyout);
    }

    [Fact]
    public void PreviewSections_UseConfigurablePreviewTextFontFamilyAndSize()
    {
        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 3600);
        AssertContainsInOrder(addSection,
            "string previewTextFontFamily = ResolvePreviewTextFontFamily();",
            "int previewTextFontSize = ResolvePreviewTextFontSize();",
            "double previewTextLineHeight = ResolvePreviewTextLineHeight(previewTextFontSize);",
            "FontFamily = new FontFamily(previewTextFontFamily)",
            "FontSize = previewTextFontSize",
            "LineHeight = previewTextLineHeight");
        Assert.DoesNotContain("FontFamily = new FontFamily(\"Consolas\")", addSection);

        string mainWindowPropertyChanged = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 6200);
        AssertContainsInOrder(mainWindowPropertyChanged,
            "e.PropertyName == nameof(ViewModel.PreviewTextFontFamily)",
            "e.PropertyName == nameof(ViewModel.PreviewTextFontSize)",
            "ApplyPreviewTextFontSettings();");

        string applyFont = ExtractMethodWindow(MainWindowSource, "ApplyPreviewTextFontSettings", window: 2600);
        AssertContainsInOrder(applyFont,
            "foreach (var block in EnumeratePreviewSectionBlocks())",
            "ApplyPreviewTextFontSettings(block, family, size, lineHeight);",
            "ApplyPreviewTextFontSettings(gutterBlock, family, size, lineHeight);",
            "InvalidateParagraphIndexCache(block);",
            "ScheduleGutterSync(block);",
            "QueueActiveMatchOverlayRefresh();");
    }

    [Fact]
    public void PreviewContextMenu_EditFileOpensEditorAtRightClickedLine()
    {
        string previewFlyout = ExtractMethodWindow(MainWindowSource, "AttachPreviewBlockContextFlyout", 4200);
        AssertContainsInOrder(previewFlyout,
            "UIElement.PointerPressedEvent",
            "CapturePreviewBlockContextPoint(block, current.Position)",
            "Text = \"Edit file\"",
            "EditPreviewFileFromContextMenuAsync(block)",
            "flyout.Items.Add(editFileItem);");

        string editCommand = ExtractMethodWindow(PreviewEditorSource, "EditPreviewFileFromContextMenuAsync", 1800);
        AssertContainsInOrder(editCommand,
            "ResolvePreviewBlockFilePath(block)",
            "TryGetPreviewBlockContextPoint(block, filePath, out var point)",
            "TryEnterPreviewEditorAtPointAsync(block, point, filePath)",
            "ResolvePreviewEditorFallbackResult(filePath)",
            "ShowFullFileEditorAsync(target, scrollToMatch: false)");

        string pointEntry = ExtractMethodWindow(PreviewEditorSource, "TryEnterPreviewEditorAtPointAsync", 4600);
        AssertContainsInOrder(pointEntry,
            "block.GetPositionFromPoint(point)",
            "ResolveLineNumberAtPointer(block, tp)",
            "ResolveSearchResultAtPreviewPoint(fileGroup, lineNum, clickedMatchIndex)",
            "ShowFullFileEditorAsync(target, scrollToMatch: true)");
    }

    [Fact]
    public void SearchStart_CollapsesAdvancedOptions()
    {
        string buttonSearch = ExtractMethodWindow(MainWindowSource, "OnSearchCancelClick", 1200);
        AssertContainsInOrder(buttonSearch,
            "if (!await CheckHddAndWarnAsync()) return;",
            "CollapseAdvancedOptionsForSearch();",
            "await ViewModel.StartSearchAsync();");

        string querySubmitted = ExtractMethodWindow(MainWindowSource, "OnQuerySubmitted", 1400);
        AssertContainsInOrder(querySubmitted,
            "if (!await CheckHddAndWarnAsync()) return;",
            "CollapseAdvancedOptionsForSearch();",
            "await ViewModel.StartSearchAsync();");

        string autoSearch = ExtractMethodWindow(MainWindowSource, "OnContentLoaded", 1800);
        AssertContainsInOrder(autoSearch,
            "if (await CheckHddAndWarnAsync())",
            "CollapseAdvancedOptionsForSearch();",
            "await ViewModel.StartSearchAsync();");

        string helper = ExtractMethodWindow(MainWindowSource, "CollapseAdvancedOptionsForSearch", 500);
        AssertContainsInOrder(helper,
            "if (AdvancedOptionsExpander.IsExpanded)",
            "AdvancedOptionsExpander.IsExpanded = false;");
    }

    [Fact]
    public void AdvancedOptionsDrawer_IsCompactWhenCollapsedAndFullWidthWhenExpanded()
    {
        string expander = ExtractXamlWindow("x:Name=\"AdvancedOptionsExpander\"", 1200);
        Assert.Contains("IsExpanded=\"False\" HorizontalAlignment=\"Left\"", expander);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", expander);
        Assert.Contains("<TextBlock Text=\"Advanced Options\"", expander);
        Assert.DoesNotContain("x:Name=\"ChevronRotate\"", expander);
        Assert.DoesNotContain("Glyph=\"&#xE76C;\"", expander);

        string expanding = ExtractMethodWindow(MainWindowSource, "OnAdvancedOptionsExpanding", 700);
        Assert.Contains("SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: true);", expanding);
        Assert.DoesNotContain("ChevronRotate.Angle", expanding);

        string collapsed = ExtractMethodWindow(MainWindowSource, "OnAdvancedOptionsCollapsed", 700);
        Assert.Contains("SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: false);", collapsed);
        Assert.DoesNotContain("ChevronRotate.Angle", collapsed);

        string widthState = ExtractMethodWindow(MainWindowSource, "SetAdvancedOptionsDrawerExpandedWidthState", 900);
        AssertContainsInOrder(widthState,
            "AdvancedOptionsExpander.HorizontalAlignment = isExpanded",
            "? HorizontalAlignment.Stretch",
            ": HorizontalAlignment.Left;",
            "AdvancedOptionsExpander.Width = double.NaN;",
            "AdvancedOptionsExpander.InvalidateMeasure();");
    }

    [Fact]
    public void TerminalChevron_HasPreSearchFallbackWhenStatusBarIsHidden()
    {
        string floatingChevron = ExtractXamlWindow("x:Name=\"PreSearchTerminalChevron\"", 900);
        Assert.Contains("Grid.RowSpan=\"7\"", floatingChevron);
        Assert.Contains("Click=\"OnToggleTerminalPane\"", floatingChevron);
        Assert.Contains("HorizontalAlignment=\"Right\"", floatingChevron);
        Assert.Contains("VerticalAlignment=\"Bottom\"", floatingChevron);
        Assert.Contains("x:Name=\"PreSearchTerminalChevronIcon\"", floatingChevron);
        Assert.Contains("ToolTipService.ToolTip=\"Toggle embedded terminal\"", floatingChevron);

        string terminalToggle = ExtractMethodWindow(MainWindowSource, "SetTerminalPaneExpanded", 1200);
        AssertContainsInOrder(terminalToggle,
            "UpdateTerminalChevronGlyphs();",
            "UpdateTerminalChevronVisibility();",
            "if (_launcherMode)",
            "PositionLauncherWindow();");

        string glyphSync = ExtractMethodWindow(MainWindowSource, "UpdateTerminalChevronGlyphs", 800);
        AssertContainsInOrder(glyphSync,
            "string glyph = _terminalPaneExpanded ? \"\\uE70D\" : \"\\uE70E\";",
            "TerminalChevronIcon.Glyph = glyph;",
            "PreSearchTerminalChevronIcon.Glyph = glyph;");

        string visibilitySync = ExtractMethodWindow(MainWindowSource, "UpdateTerminalChevronVisibility", 1000);
        AssertContainsInOrder(visibilitySync,
            "bool statusBarChevronVisible = StatusBarRow.Height.IsAuto || StatusBarRow.Height.Value > 0;",
            "TerminalChevron.Visibility = statusBarChevronVisible ? Visibility.Visible : Visibility.Collapsed;",
            "PreSearchTerminalChevron.Visibility = statusBarChevronVisible ? Visibility.Collapsed : Visibility.Visible;");

        string statusBarVisibility = ExtractMethodWindow(MainWindowSource, "UpdateBottomStatusBarVisibility", 900);
        Assert.Contains("UpdateTerminalChevronVisibility();", statusBarVisibility);
    }

    [Fact]
    public void QueryBox_ClearButtonVisibilityFollowsQueryTextEvenWithoutFocus()
    {
        string inlineToggles = ExtractXamlWindow("x:Name=\"InlineSearchToggles\"", 300);
        Assert.Contains("Margin=\"0,0,36,0\"", inlineToggles);

        string searchBar = ExtractXamlWindow("x:Name=\"QueryClearButton\"", 1100);
        Assert.Contains("Width=\"32\" Height=\"32\"", searchBar);
        Assert.Contains("Margin=\"0,0,4,0\"", searchBar);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.HasQueryText, Mode=OneWay}\"", searchBar);
        Assert.Contains("Click=\"OnQueryClearClick\"", searchBar);
        Assert.Contains("ToolTipService.ToolTip=\"Clear query\"", searchBar);
        Assert.Contains("<FontIcon Glyph=\"&#xE894;\"", searchBar);

        string clearHandler = ExtractMethodWindow(MainWindowSource, "OnQueryClearClick", 700);
        AssertContainsInOrder(clearHandler,
            "SuppressQuerySuggestionsFor(250, QueryBox);",
            "QueryBox.Text = string.Empty;",
            "ViewModel.Query = string.Empty;",
            "QueryBox.Focus(FocusState.Programmatic);");
    }

    [Fact]
    public void ExitLauncherMode_RestoresSnapFriendlyWindowChrome()
    {
        string exitLauncher = ExtractMethodWindow(MainWindowSource, "ExitLauncherMode", 2600);
        AssertContainsInOrder(exitLauncher,
            "_launcherMode = false;",
            "op.SetBorderAndTitleBar(true, true);",
            "op.IsResizable = true;",
            "op.IsMaximizable = true;",
            "op.IsMinimizable = true;",
            "SetNativeCaptionButtonsVisible(true);");

        string launcherChrome = ExtractMethodWindow(MainWindowSource, "RestoreToLauncherChrome", 1800);
        AssertContainsInOrder(launcherChrome,
            "op.SetBorderAndTitleBar(true, false);",
            "op.IsMaximizable = false;",
            "SetNativeCaptionButtonsVisible(false);");
    }

    [Fact]
    public void PreviewPanel_MouseWheelCanScrollPreviewSurface()
    {
        string previewScrollViewer = ExtractXamlWindow("x:Name=\"PreviewScrollViewer\"", 500);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", previewScrollViewer);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", previewScrollViewer);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", previewScrollViewer);

        string horizontalScrollHelper = ExtractMethodWindow(MainWindowSource, "SetHorizontalPreviewScroll", 700);
        AssertContainsInOrder(horizontalScrollHelper,
            "scrollViewer.HorizontalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;",
            "scrollViewer.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;");

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", 6500);
        AssertContainsInOrder(addSection,
            "var sectionScroller = new ScrollViewer",
            "HorizontalScrollMode = ViewModel.PreviewWordWrap ? ScrollMode.Disabled : ScrollMode.Enabled",
            "HorizontalScrollBarVisibility = ViewModel.PreviewWordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto");

        string hook = ExtractMethodWindow(MainWindowSource, "EnsurePreviewViewChangedHooked", 1800);
        AssertContainsInOrder(hook,
            "PreviewScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent,",
            "new PointerEventHandler(OnPreviewPointerWheelChanged)",
            "handledEventsToo: true);");

        string wheel = ExtractMethodWindow(MainWindowSource, "OnPreviewPointerWheelChanged", 1700);
        AssertContainsInOrder(wheel,
            "NotePreviewManualScrollInput(\"wheel\");",
            "double horizontalOffsetBefore = PreviewScrollViewer.HorizontalOffset;",
            "double verticalOffsetBefore = PreviewScrollViewer.VerticalOffset;",
            "ApplyPreviewPointerWheelFallback(delta, horizontalWheel, horizontalOffsetBefore, verticalOffsetBefore)");

        string fallback = ExtractMethodWindow(MainWindowSource, "ApplyPreviewPointerWheelFallback", 3000);
        AssertContainsInOrder(fallback,
            "if (Math.Abs(PreviewScrollViewer.VerticalOffset - verticalOffsetBefore) > 0.5)",
            "double targetY = Math.Clamp(verticalOffsetBefore - delta, 0, PreviewScrollViewer.ScrollableHeight);",
            "PreviewScrollViewer.ChangeView(null, targetY, null, disableAnimation: true);");
    }

    [Fact]
    public void NoWrapPreviewSelectionDrag_AutoScrollsHorizontally()
    {
        string ctor = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 2200);
        Assert.Contains("AttachPreviewSelectionAutoScroll(PreviewBlock);", ctor);
        Assert.Contains("ConfigurePreviewSelectionMode(PreviewBlock);", ctor);

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 8000);
        Assert.Contains("AttachPreviewSelectionAutoScroll(block);", addSection);
        Assert.Contains("IsTextSelectionEnabled = wrap,", addSection);

        string attach = ExtractMethodWindow(MainWindowSource, "AttachPreviewSelectionAutoScroll", window: 2600);
        AssertContainsInOrder(attach,
            "UIElement.PointerPressedEvent",
            "OnPreviewSelectionAutoScrollPointerPressed",
            "UIElement.PointerMovedEvent",
            "OnPreviewSelectionAutoScrollPointerMoved",
            "UIElement.PointerReleasedEvent",
            "OnPreviewSelectionAutoScrollPointerEnded",
            "UIElement.PointerCanceledEvent",
            "OnPreviewSelectionAutoScrollPointerEnded",
            "UIElement.PointerCaptureLostEvent",
            "OnPreviewSelectionAutoScrollPointerEnded");

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyPreviewSelectionAutoScroll", window: 4600);
        AssertContainsInOrder(apply,
            "block.TextWrapping != TextWrapping.NoWrap",
            "scroller.HorizontalScrollMode != ScrollMode.Enabled",
            "TryGetPreviewSelectionAutoScrollVelocity(scroller, _previewSelectionAutoScrollPointerX, out double velocity)",
            "double step = velocity * elapsedSeconds;",
            "double targetX = Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth);",
            "scroller.ChangeView(targetX, null, null, disableAnimation: true);",
            "UpdatePreviewCustomSelectionFromCurrentPointer();");

        string timer = ExtractMethodWindow(MainWindowSource, "EnsurePreviewSelectionAutoScrollTimer", window: 2200);
        AssertContainsInOrder(timer,
            "_previewSelectionAutoScrollTimer ??= new Timer(",
            "OnPreviewSelectionAutoScrollTimerElapsed",
            "PreviewSelectionAutoScrollTimerIntervalMs",
            "LogPreviewSelectionAutoScrollTimerState(\"high-timer-start\");");

        string elapsed = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollTimerElapsed", window: 2200);
        AssertContainsInOrder(elapsed,
            "Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 1)",
            "DispatcherQueuePriority.High",
            "OnPreviewSelectionAutoScrollTimerTick();");

        string press = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollPointerPressed", window: 1800);
        Assert.Contains("block.CapturePointer(e.Pointer);", press);
        Assert.Contains("BeginPreviewCustomSelection(block, scroller);", press);
        Assert.Contains("e.Handled = true;", press);

        string selectionMode = ExtractMethodWindow(MainWindowSource, "ConfigurePreviewSelectionMode", window: 900);
        AssertContainsInOrder(selectionMode,
            "block.TextWrapping == TextWrapping.Wrap",
            "block.IsTextSelectionEnabled = useNativeSelection;");

        string highlighter = ExtractMethodWindow(MainWindowSource, "UpdatePreviewCustomSelectionHighlighter", window: 2200);
        AssertContainsInOrder(highlighter,
            "new TextHighlighter",
            "new TextRange",
            "block.TextHighlighters.Add(_previewCustomSelectionHighlighter);");

        string copy = ExtractMethodWindow(MainWindowSource, "CopyPreviewSelection", window: 1200);
        Assert.Contains("TryBuildPreviewCustomSelectionText(block, withLineNumbers, out string customSelectedText)", copy);
    }

    [Fact]
    public void ResultsListScroll_PreservesPinnedTopDuringLiveUpdates()
    {
        string ctor = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 2600);
        Assert.Contains("InitializeResultsListSmartScroll();", ctor);

        string initialize = ExtractMethodWindow(MainWindowSource, "InitializeResultsListSmartScroll", window: 2200);
        AssertContainsInOrder(initialize,
            "ViewModel.ResultGroupsChanging += OnResultGroupsChanging;",
            "ViewModel.ResultGroups.CollectionChanged += OnResultGroupsCollectionChanged;",
            "ResultsList.Loaded +=",
            "EnsureResultsListScrollViewerHooked();",
            "CaptureResultsListScrollPosition();");

        string changing = ExtractMethodWindow(MainWindowSource, "OnResultGroupsChanging", window: 1200);
        AssertContainsInOrder(changing,
            "CaptureResultsListScrollPosition();",
            "ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();",
            "ResultGroupsChanging: intent={intent}",
            "QueueResultsListSmartScrollRestore(intent);");

        string collectionChanged = ExtractMethodWindow(MainWindowSource, "OnResultGroupsCollectionChanged", window: 1800);
        AssertContainsInOrder(collectionChanged,
            "ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();",
            "QueueResultsListSmartScrollRestore(intent);");

        string resolve = ExtractMethodWindow(MainWindowSource, "ResolveResultsListSmartScrollIntent", window: 1200);
        AssertContainsInOrder(resolve,
            "if (_resultsListShowMoreRestoreInProgress)",
            "return ResultsListSmartScrollIntent.None;",
            "if (_resultsListWasAtTop)",
            "ResultsListSmartScrollIntent.KeepTop",
            "if (_autoScrollEnabled)",
            "ResultsListSmartScrollIntent.FollowBottom");

        string capture = ExtractMethodWindow(MainWindowSource, "CaptureResultsListScrollPosition", window: 1400);
        AssertContainsInOrder(capture,
            "bool hasGroupsWithoutScroller = ViewModel.ResultGroups.Count > 0;",
            "_resultsListWasAtTop = hasGroupsWithoutScroller;",
            "_resultsListWasAtBottom = hasGroupsWithoutScroller;",
            "bool hasVisibleGroups = ViewModel.ResultGroups.Count > 0;",
            "_resultsListWasAtTop = hasVisibleGroups && IsResultsListAtTop(scroller);",
            "_resultsListWasAtBottom = hasVisibleGroups && IsResultsListAtBottom(scroller);");
        Assert.DoesNotContain("IsFirstResultGroupAtTop", MainWindowSource);

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyResultsListSmartScrollIntent", window: 1700);
        AssertContainsInOrder(apply,
            "if (intent == ResultsListSmartScrollIntent.KeepTop)",
            "ScrollResultsListToTop();",
            "remainingPasses > 0",
            "ApplyResultsListSmartScrollIntent(intent, remainingPasses - 1)");

        string scrollTop = ExtractMethodWindow(MainWindowSource, "ScrollResultsListToTop", window: 1400);
        AssertContainsInOrder(scrollTop,
            "ResultsList.ScrollIntoView(ViewModel.ResultGroups[0], ScrollIntoViewAlignment.Leading);",
            "_resultsListScrollViewer?.ChangeView(null, 0, null, disableAnimation: true);",
            "ScrollResultsListToTop: groups={ViewModel.ResultGroups.Count}");

        string autoScroll = ExtractMethodWindow(MainWindowSource, "OnAutoScrollTick", window: 600);
        Assert.Contains("if (_resultsListTopRestoreInProgress) return;", autoScroll);
        Assert.Contains("if (_resultsListShowMoreRestoreInProgress) return;", autoScroll);
        Assert.Contains("if (_resultsListWasAtTop) return;", autoScroll);
        Assert.Contains("ScrollResultsListToBottom();", autoScroll);

        string showMore = ExtractMethodWindow(MainWindowSource, "OnShowMoreClicked", window: 2200);
        AssertContainsInOrder(showMore,
            "double? restoreVerticalOffset = CaptureResultsListVerticalOffset();",
            "_resultsListShowMoreRestoreInProgress = restoreVerticalOffset.HasValue;",
            "int shown = await ShowMoreVisibleResultsIncrementalAsync(g, FileGroup.PageSize, restoreVerticalOffset).ConfigureAwait(true);",
            "QueueRestoreResultsListVerticalOffsetAfterShowMore(restoreVerticalOffset, g.FilePath);",
            "_resultsListShowMoreRestoreInProgress = false;",
            "CaptureResultsListScrollPosition();",
            "catch (Exception ex)",
            "_resultsListShowMoreRestoreInProgress = false;",
            "OnShowMoreClicked failed");
        Assert.DoesNotContain("QueueCenterNewlyShownResult", MainWindowSource);
        Assert.DoesNotContain("CenterVisibleResultInResultsList", MainWindowSource);

        string showMoreIncremental = ExtractMethodWindow(MainWindowSource, "ShowMoreVisibleResultsIncrementalAsync", window: 2600);
        AssertContainsInOrder(showMoreIncremental,
            "double? restoreVerticalOffset = null",
            "int shown = group.ShowMore(end - start);",
            "if (restoreVerticalOffset is double pinnedOffset)",
            "ApplyResultsListVerticalOffsetAfterShowMore(pinnedOffset, group.FilePath, log: false);",
            "if (remainingToShow > 0 && group.HasMore)",
            "await Task.Yield();");

        string showMoreRestore = ExtractMethodWindow(MainWindowSource, "RestoreResultsListVerticalOffsetAfterShowMore", window: 2200);
        AssertContainsInOrder(showMoreRestore,
            "ApplyResultsListVerticalOffsetAfterShowMore(targetOffset, filePath, log: remainingPasses == ResultsListSmartScrollRestorePasses + 2)",
            "remainingPasses > 0",
            "RestoreResultsListVerticalOffsetAfterShowMore(targetOffset, filePath, remainingPasses - 1)",
            "_resultsListShowMoreRestoreInProgress = false;");

        string showMoreApply = ExtractMethodWindow(MainWindowSource, "ApplyResultsListVerticalOffsetAfterShowMore", window: 1800);
        AssertContainsInOrder(showMoreApply,
            "double clampedOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scroller.ScrollableHeight));",
            "scroller.ChangeView(null, clampedOffset, null, disableAnimation: true);",
            "CaptureResultsListScrollPosition();",
            "return true;");

        string showMoreXaml = ExtractXamlWindow("Click=\"OnShowMoreClicked\"", 300);
        Assert.Contains("Tapped=\"OnShowMoreTapped\"", showMoreXaml);

        string showMoreTapped = ExtractMethodWindow(MainWindowSource, "OnShowMoreTapped", window: 500);
        Assert.Contains("e.Handled = true;", showMoreTapped);

        string collapsed = ExtractMethodWindow(MainWindowSource, "OnFileGroupCollapsed", window: 1600);
        AssertContainsInOrder(collapsed,
            "DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low",
            "if (!g.IsExpanded)",
            "g.ClearVisibleResults();");

        string expanding = ExtractMethodWindow(MainWindowSource, "OnFileGroupExpanding", window: 2200);
        AssertContainsInOrder(expanding,
            "g.MaterializeEvictedStubs();",
            "await EnsureVisibleResultsForExpandedGroupSerializedAsync(g, \"expanding\").ConfigureAwait(true);",
            "if (!ReferenceEquals(sender.DataContext, g))",
            "InvalidateListViewItemContainer(sender);",
            "sender.InvalidateMeasure();");
        Assert.DoesNotContain("sender.UpdateLayout();", expanding);

        string serializedEnsure = ExtractMethodWindow(MainWindowSource, "EnsureVisibleResultsForExpandedGroupSerializedAsync", window: 1600);
        AssertContainsInOrder(serializedEnsure,
            "if (!_visibleResultsEnsureInProgress.Add(group))",
            "EnsureVisible skipped duplicate",
            "await EnsureVisibleResultsForExpandedGroupAsync(group).ConfigureAwait(true);",
            "_visibleResultsEnsureInProgress.Remove(group);");

        string containerChanging = ExtractMethodWindow(MainWindowSource, "OnResultsListContainerContentChanging", window: 900);
        Assert.Contains("EnsureVisibleResultsForExpandedGroupFromContainerAsync(g)", containerChanging);

        string containerEnsure = ExtractMethodWindow(MainWindowSource, "EnsureVisibleResultsForExpandedGroupFromContainerAsync", window: 1200);
        AssertContainsInOrder(containerEnsure,
            "try",
            "await EnsureVisibleResultsForExpandedGroupSerializedAsync(group, \"container\").ConfigureAwait(true);",
            "catch (Exception ex)",
            "EnsureVisibleResultsForExpandedGroupAsync failed");
    }

    [Fact]
    public void PreviewSectionContentBackgrounds_AreConfigurableAndDefaultSelectedToBlack()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("DefaultSelectedPreviewContentBackgroundColor = \"#FF000000\"", settingsSource);
        Assert.Contains("DefaultUnselectedPreviewContentBackgroundColor = \"#00000000\"", settingsSource);

        Assert.Contains("Selected preview content background:", SettingsWindowSource);
        Assert.Contains("Unselected preview content background:", SettingsWindowSource);
        Assert.Contains("new ColorPicker", SettingsWindowSource);
        Assert.Contains("IsAlphaEnabled = true", SettingsWindowSource);

        string highlight = ExtractMethodWindow(MainWindowSource, "HighlightActiveExpander", 1800);
        AssertContainsInOrder(highlight,
            "child.Background = null;",
            "ApplyPreviewSectionContentBackground(child, isActive);");
        Assert.DoesNotContain("s_activeExpanderBrush", MainWindowSource);

        string backgroundHelper = ExtractMethodWindow(MainWindowSource, "ApplyPreviewSectionContentBackground", 2600);
        AssertContainsInOrder(backgroundHelper,
            "CreatePreviewSectionContentBackgroundBrush(isActive)",
            "grid.Background = brush;",
            "scroller.Background = brush;",
            "contentBorder.Background = brush;");
    }

    [Fact]
    public void GutterTextColors_AreConfigurableForPreviewAndEditor()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        string textControlBoxSource = File.ReadAllText(Path.Combine(RepoRoot, "vendor", "TextControlBox-WinUI", "TextControlBox", "TextControlBox.cs"));

        Assert.Contains("DefaultPreviewGutterColor = \"#FF9CDCFE\"", settingsSource);
        Assert.Contains("PreviewEditorGutterColor", settingsSource);
        Assert.Contains("MigrateLegacyPreviewGutterColors(settings);", settingsSource);

        Assert.Contains("PreviewEditorGutterColor = ColorStringHelper.Normalize", viewModelSource);
        Assert.Contains("_settings.PreviewEditorGutterColor = ColorStringHelper.Normalize", viewModelSource);

        Assert.Contains("Preview gutter text:", SettingsWindowSource);
        Assert.Contains("Matched preview gutter text:", SettingsWindowSource);
        Assert.Contains("Editor gutter text:", SettingsWindowSource);
        Assert.Contains("Width = 160", SettingsWindowSource);
        Assert.Contains("MaxWidth = 180", SettingsWindowSource);

        string applyColors = ExtractMethodWindow(MainWindowSource, "ApplyPreviewColors", window: 1800);
        AssertContainsInOrder(applyColors,
            "var previewGutterColor = ColorStringHelper.Parse(vm.PreviewGutterContextColor",
            "s_contextGutterBrush.Color = previewGutterColor;",
            "s_gutterSepBrush.Color = previewGutterColor;",
            "PreviewEditor.LineNumberColor = ColorStringHelper.Parse(vm.PreviewEditorGutterColor");

        Assert.Contains("public Windows.UI.Color LineNumberColor", textControlBoxSource);
    }

    [Fact]
    public void DeveloperOptions_HidesMemoryPressureWarningLabelByDefault()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowMemoryPressureWarningLabel { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowMemoryPressureWarningLabel { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("[ObservableProperty] public partial bool ShowMemoryPressureWarningLabel { get; set; }", viewModelSource);
        Assert.DoesNotContain("ShowMemoryPressureWarningLabel { get; set; } = true", viewModelSource);
        Assert.Contains("ShowMemoryPressureWarningLabel = _settings.ShowMemoryPressureWarningLabel;", viewModelSource);
        Assert.Contains("_settings.ShowMemoryPressureWarningLabel = ShowMemoryPressureWarningLabel;", viewModelSource);
        AssertContainsInOrder(viewModelSource,
            "MemoryPressureWarningVisibility =>",
            "ShowMemoryPressureWarningLabel && !string.IsNullOrWhiteSpace(DegradedNoticeText)",
            "Microsoft.UI.Xaml.Visibility.Visible",
            "Microsoft.UI.Xaml.Visibility.Collapsed");
        Assert.Contains("OnShowMemoryPressureWarningLabelChanged", viewModelSource);

        string resultsToolbar = ExtractXamlWindow("Text=\"{x:Bind ViewModel.DegradedNoticeText", 360);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.MemoryPressureWarningVisibility, Mode=OneWay}\"", resultsToolbar);

        Assert.Contains("AddTab(\"Developer Options\")", SettingsWindowSource);
        Assert.Contains("Show memory pressure warning label", SettingsWindowSource);
        Assert.Contains("IsChecked = _viewModel.ShowMemoryPressureWarningLabel", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowMemoryPressureWarningLabel = true", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowMemoryPressureWarningLabel = false", SettingsWindowSource);
    }

    [Fact]
    public void DeveloperOptions_ControlAutoScrollCheckboxVisibility()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowAutoScrollResultsCheckbox { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowAutoScrollResultsCheckbox { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("ShowAutoScrollResultsCheckbox = _settings.ShowAutoScrollResultsCheckbox;", viewModelSource);
        Assert.Contains("_settings.ShowAutoScrollResultsCheckbox = ShowAutoScrollResultsCheckbox;", viewModelSource);
        AssertContainsInOrder(viewModelSource,
            "AutoScrollResultsCheckboxVisibility =>",
            "ShowAutoScrollResultsCheckbox",
            "Microsoft.UI.Xaml.Visibility.Visible",
            "Microsoft.UI.Xaml.Visibility.Collapsed");
        Assert.Contains("OnShowAutoScrollResultsCheckboxChanged", viewModelSource);

        string toolbar = ExtractXamlWindow("x:Name=\"AutoScrollResultsCheckBox\"", 700);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.AutoScrollResultsCheckboxVisibility, Mode=OneWay}\"", toolbar);

        Assert.Contains("Show Auto-scroll checkbox", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowAutoScrollResultsCheckbox = true", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowAutoScrollResultsCheckbox = false", SettingsWindowSource);

        Assert.Contains("AutoScrollResultsCheckBox.Visibility == Visibility.Visible", MainWindowSource);
    }

    [Fact]
    public void DeveloperOptions_HidesStatsForNerdsByDefault()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowStatsForNerds { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowStatsForNerds { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("[ObservableProperty] public partial bool ShowStatsForNerds { get; set; }", viewModelSource);
        Assert.DoesNotContain("ShowStatsForNerds { get; set; } = true", viewModelSource);
        Assert.Contains("ShowStatsForNerds = _settings.ShowStatsForNerds;", viewModelSource);
        Assert.Contains("_settings.ShowStatsForNerds = ShowStatsForNerds;", viewModelSource);
        AssertContainsInOrder(viewModelSource,
            "StatsForNerdsVisibility =>",
            "ShowStatsForNerds",
            "Microsoft.UI.Xaml.Visibility.Visible",
            "Microsoft.UI.Xaml.Visibility.Collapsed");
        Assert.Contains("OnShowStatsForNerdsChanged", viewModelSource);

        string statusBar = ExtractXamlWindow("ViewModel.StatsForNerdsVisibility", 900);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.StatsForNerdsVisibility, Mode=OneWay}\"", statusBar);

        Assert.Contains("Stats for nerds", SettingsWindowSource);
        Assert.Contains("IsChecked = _viewModel.ShowStatsForNerds", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowStatsForNerds = true", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowStatsForNerds = false", SettingsWindowSource);
    }

    [Fact]
    public void ResultsToolbar_OptionsAreHostedInEllipsisFlyout()
    {
        string toolbar = ExtractXamlWindow("x:Name=\"AutoScrollResultsCheckBox\"", 5200);
        Assert.Contains("x:Name=\"ResultsOptionsButton\"", toolbar);
        Assert.Contains("Glyph=\"&#xE700;\"", toolbar);
        Assert.Contains("Background=\"Transparent\" BorderThickness=\"0\"", toolbar);
        Assert.DoesNotContain("Background=\"#202020\"", toolbar);
        Assert.DoesNotContain("BorderBrush=\"#404040\"", toolbar);
        Assert.Contains("ToolTipService.ToolTip=\"Results options\"", toolbar);
        Assert.Contains("<Flyout x:Name=\"ResultsOptionsFlyout\" Placement=\"BottomEdgeAlignedRight\">", toolbar);

        AssertContainsInOrder(toolbar,
            "x:Name=\"ResultsOptionsButton\"",
            "<Button.Flyout>",
            "x:Name=\"ResultsOptionsFlyout\"",
            "Text=\"Context:\"",
            "Value=\"{x:Bind ViewModel.ContextLines, Mode=TwoWay}\"",
            "SpinButtonPlacementMode=\"Hidden\"",
            "Click=\"OnClearResults\"",
            "Text=\"Clear all results\"",
            "x:Name=\"SaveSessionButton\"",
            "Text=\"Save session\"",
            "x:Name=\"LoadSessionButton\"",
            "Text=\"Load session\"");

        Assert.DoesNotContain("SpinButtonPlacementMode=\"Compact\"", toolbar);
        Assert.DoesNotContain("ToolTipService.ToolTip=\"Clear all results (Ctrl+Shift+Delete)\">\r\n                                <FontIcon Glyph=\"&#xE74D;\"", toolbar);

        string clearResults = ExtractMethodWindow(MainWindowSource, "OnClearResults", 1400);
        AssertContainsInOrder(clearResults,
            "ResultsOptionsFlyout.Hide();",
            "await ViewModel.ClearResultsAsync();");
    }

    [Fact]
    public void SortFlyout_UsesInlineArrowButtonsForEachSortField()
    {
        string sortFlyout = ExtractXamlWindow("<Flyout x:Name=\"SortFlyout\"", 9000);

        Assert.DoesNotContain("MenuFlyoutSubItem Text=\"# Matches\"", sortFlyout);
        Assert.DoesNotContain("MenuFlyoutSubItem Text=\"Date Modified\"", sortFlyout);
        Assert.DoesNotContain("MenuFlyoutSubItem Text=\"File Size\"", sortFlyout);
        Assert.DoesNotContain("MenuFlyoutSubItem Text=\"File Name\"", sortFlyout);
        Assert.DoesNotContain("DirectionDropDown", sortFlyout);
        Assert.DoesNotContain("<MenuFlyoutItem Text=\"Desc\" Click=\"OnSort", sortFlyout);
        Assert.DoesNotContain("<MenuFlyoutItem Text=\"Asc\" Click=\"OnSort", sortFlyout);

        AssertContainsInOrder(sortFlyout,
            "<TextBlock Text=\"# Matches\"",
            "x:Name=\"SortMatchesAscButton\"",
            "Glyph=\"&#xE74A;\"",
            "x:Name=\"SortMatchesDescButton\"",
            "Glyph=\"&#xE74B;\"",
            "<TextBlock Text=\"Date Modified\"",
            "x:Name=\"SortDateModifiedAscButton\"",
            "x:Name=\"SortDateModifiedDescButton\"",
            "<TextBlock Text=\"File Size\"",
            "x:Name=\"SortFileSizeAscButton\"",
            "x:Name=\"SortFileSizeDescButton\"",
            "<TextBlock Text=\"File Name\"",
            "x:Name=\"SortFileNameAscButton\"",
            "x:Name=\"SortFileNameDescButton\"");

        string opening = ExtractMethodWindow(MainWindowSource, "OnSortFlyoutOpening", 900);
        Assert.Contains("RefreshSortDirectionButtons();", opening);

        string refresh = ExtractMethodWindow(MainWindowSource, "RefreshSortDirectionButtons", 900);
        AssertContainsInOrder(refresh,
            "UpdateSortDirectionButtons(SortMatchesAscButton, SortMatchesDescButton, 1);",
            "UpdateSortDirectionButtons(SortDateModifiedAscButton, SortDateModifiedDescButton, 2);",
            "UpdateSortDirectionButtons(SortFileSizeAscButton, SortFileSizeDescButton, 3);",
            "UpdateSortDirectionButtons(SortFileNameAscButton, SortFileNameDescButton, 4);");

        string helper = ExtractMethodWindow(MainWindowSource, "UpdateSortDirectionButtons", 900);
        AssertContainsInOrder(opening,
            "RefreshSortDirectionButtons();");
        AssertContainsInOrder(helper,
            "ViewModel.GetSortDirectionIndex(sortModeIndex)",
            "ApplySortArrowState(ascButton, selected: direction == 1);",
            "ApplySortArrowState(descButton, selected: direction == 0);");

        string arrowState = ExtractMethodWindow(MainWindowSource, "ApplySortArrowState", 700);
        AssertContainsInOrder(arrowState,
            "selected",
            "Microsoft.UI.Colors.White",
            "Microsoft.UI.ColorHelper.FromArgb");

        string toggle = ExtractMethodWindow(MainWindowSource, "ToggleSortDirection", 800);
        AssertContainsInOrder(toggle,
            "ViewModel.GetSortDirectionIndex(sortModeIndex) == sortDirectionIndex",
            "ViewModel.RemoveSortSelection(sortModeIndex);",
            "ViewModel.ApplySortSelection(sortModeIndex, sortDirectionIndex);",
            "RefreshSortDirectionButtons();");
        Assert.DoesNotContain("ViewModel.SortModeIndex = sortModeIndex", toggle);
    }

    [Fact]
    public void ResultsToolbar_FilterDropdownHostsDateAndExtensionFilters()
    {
        string toolbar = ExtractXamlWindow("ToolTipService.ToolTip=\"Filter results\"", 5200);
        AssertContainsInOrder(toolbar,
            "<FontIcon Glyph=\"&#xE71C;\"",
            "<TextBlock Text=\"Filter\"",
            "<MenuFlyout Opening=\"OnFilterFlyoutOpening\">",
            "<MenuFlyoutSubItem Text=\"By date\">",
            "<MenuFlyoutItem Text=\"Any date\" Click=\"OnDateFilterNone\" />",
            "<MenuFlyoutItem Text=\"Last 5 years\" Click=\"OnDateFilterPastFiveYears\" />",
            "<MenuFlyoutSubItem x:Name=\"ExtensionFilterSubMenu\" Text=\"By extension\" />");
        Assert.DoesNotContain("OnFilterByExtension", toolbar);

        string filterRow = ExtractXamlWindow("PlaceholderText=\"Filter files…\"", 1700);
        Assert.DoesNotContain("Content=\"{x:Bind ViewModel.DateRangeFilterLabel", filterRow);

        string extensionMenu = ExtractMethodWindow(MainWindowSource, "RebuildExtensionFilterSubMenu", window: 3200);
        AssertContainsInOrder(extensionMenu,
            "ExtensionFilterSubMenu.Items.Clear();",
            "var options = ViewModel.GetExtensionFilterOptions();",
            "Text = \"No extensions available\"",
            "var clearItem = new MenuFlyoutItem { Text = \"All extensions\" };",
            "new ToggleMenuFlyoutItem",
            "item.Click += OnExtensionFilterItemClicked;",
            "ExtensionFilterSubMenu.Items.Add(item);");
        Assert.DoesNotContain("ContentDialog", extensionMenu);

        string extensionToggle = ExtractMethodWindow(MainWindowSource, "OnExtensionFilterItemClicked", window: 2200);
        AssertContainsInOrder(extensionToggle,
            "var options = ViewModel.GetExtensionFilterOptions();",
            ".ToHashSet(StringComparer.OrdinalIgnoreCase);",
            "selectedExtensions.Add(extension);",
            "selectedExtensions.Remove(extension);",
            "ViewModel.ClearExtensionFilter();",
            "ViewModel.SetExtensionFilter(selectedExtensions);");
        Assert.DoesNotContain("ContentDialog", extensionToggle);
    }

    [Fact]
    public void EmbeddedTerminal_RendersAboveBottomStatusBar()
    {
        AssertContainsInOrder(MainWindowXaml,
            "<RowDefinition x:Name=\"SplitPaneRow\" Height=\"*\" />",
            "<RowDefinition x:Name=\"TerminalRow\" Height=\"0\" />",
            "<RowDefinition x:Name=\"StatusBarRow\" Height=\"Auto\" />");

        AssertContainsInOrder(MainWindowXaml,
            "<!-- Bottom status bar -->",
            "<Grid Grid.Row=\"6\"");
        Assert.Contains("<Grid x:Name=\"TerminalHost\" Grid.Row=\"5\"", MainWindowXaml);
        Assert.Contains("<WebView2 x:Name=\"TerminalWebView\"", MainWindowXaml);
        AssertContainsInOrder(TerminalHtml,
            "#terminal {",
            "width: 100%;",
            "height: 100%;",
            "box-sizing: border-box;",
            "padding: 8px 12px;");
    }

    [Fact]
    public void ResultsList_GroupHeadersUseExpandableMixedRows()
    {
        Assert.Contains("<DataTemplate x:Key=\"ResultGroupHeaderTemplate\" x:DataType=\"models:ResultGroupHeaderRow\">", MainWindowXaml);
        Assert.Contains("Click=\"OnResultGroupHeaderClicked\"", MainWindowXaml);
        Assert.Contains("Glyph=\"{x:Bind ChevronGlyph, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind Title}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind SummaryText}\"", MainWindowXaml);
        Assert.Contains("Glyph=\"{x:Bind ChevronGlyph, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind SummaryText}\"", MainWindowXaml);
        Assert.Contains("<DataTemplate x:Key=\"FileGroupResultTemplate\" x:DataType=\"models:FileGroup\">", MainWindowXaml);
        AssertContainsInOrder(MainWindowXaml,
            "<local:ResultListItemTemplateSelector x:Key=\"ResultListItemTemplateSelector\"",
            "GroupHeaderTemplate=\"{StaticResource ResultGroupHeaderTemplate}\"",
            "FileGroupTemplate=\"{StaticResource FileGroupResultTemplate}\"");
        AssertContainsInOrder(MainWindowXaml,
            "ItemsSource=\"{x:Bind ViewModel.ResultRows, Mode=OneWay}\"",
            "ItemTemplateSelector=\"{StaticResource ResultListItemTemplateSelector}\"");
        Assert.DoesNotContain("Text=\"{x:Bind GroupHeaderText", MainWindowXaml);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        AssertContainsInOrder(viewModelSource,
            "public BatchObservableCollection<object> ResultRows { get; } = new();",
            "public void ToggleResultGroupExpansion(ResultGroupHeaderRow header)",
            "private void RebuildResultRows()",
            "ResultRows.ReplaceAll(rows);");

        string scrollSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.ResultsListScroll.cs"));
        Assert.Contains("ViewModel.ResultRows.CollectionChanging += OnResultGroupsChanging;", scrollSource);
        Assert.Contains("ViewModel.ResultRows.CollectionChanged += OnResultGroupsCollectionChanged;", scrollSource);
        Assert.DoesNotContain("ViewModel.ResultGroups.CollectionChanged += OnResultGroupsCollectionChanged;", scrollSource);

        string fileGroupTemplate = ExtractXamlWindow("<DataTemplate x:Key=\"FileGroupResultTemplate\"", 4200);
        Assert.Contains("Visibility=\"{x:Bind HasContentMatches, Mode=OneWay}\"", fileGroupTemplate);
    }

    [Fact]
    public void FileNameOnlyPreview_RendersFullFileWithoutMatchHighlightsOrNavigation()
    {
        string previewable = ExtractMethodWindow(MainWindowSource, "GetPreviewableResults", window: 900);
        AssertContainsInOrder(previewable,
            "var previewable = results.ToList();",
            "previewable.Any(result => result.LineNumber > 0)",
            "previewable.RemoveAll(result => result.LineNumber <= 0)",
            "return previewable;");

        string previewSingle = ExtractMethodWindow(MainWindowSource, "OnPreviewSingleFile", window: 2400);
        Assert.Contains("GetPreviewableResults(group)", previewSingle);
        Assert.DoesNotContain("r.LineNumber == 0", previewSingle);

        string previewSelected = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectedFiles", window: 3600);
        Assert.Contains("GetPreviewableResults(g)", previewSelected);
        Assert.DoesNotContain("r.LineNumber == 0", previewSelected);

        string singleFilePreview = ExtractMethodWindow(MainWindowSource, "ShowSingleFilePreviewAsync", window: 5200);
        AssertContainsInOrder(singleFilePreview,
            "bool isFileNameOnlyPreview = r.LineNumber <= 0;",
            "fullFile |= isFileNameOnlyPreview;",
            "Regex? rx = isFileNameOnlyPreview",
            "? null",
            "var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);",
            "bool isMatchLine = !isFileNameOnlyPreview && lineNum == r.LineNumber;");

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection", window: 5200);
        AssertContainsInOrder(concatenated,
            "bool isFileNameOnlyPreview = r.LineNumber <= 0;",
            "fullFile: isFileNameOnlyPreview",
            "bool isMatchLine = !isFileNameOnlyPreview && lineNum == r.LineNumber;",
            "isFileNameOnlyPreview ? null : rx",
            "isMatchLine ? _matchParagraphs : null");

        string highlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 1000);
        AssertContainsInOrder(highlight,
            "if (results.All(result => result.LineNumber <= 0))",
            "BuildConcatenatedSection(section, results, allLines, previewLines, rx: null);",
            "return;");

        string computeCount = ExtractMethodWindow(MainWindowSource, "ComputeMatchCount", window: 1000);
        AssertContainsInOrder(computeCount,
            "if (results.All(result => result.LineNumber <= 0))",
            "return 0;",
            "results = results.Where(result => result.LineNumber > 0).ToList();");

        Assert.Contains("RegisterSectionMatchTotal(block, CountContentMatchResults(results));", MainWindowSource);

        Assert.Contains("private static List<SearchResult> GetPreviewableResults(FileGroup group)", MainWindowSource);
        Assert.Contains("group.GetPreviewSnapshot(limit)", MainWindowSource);
        Assert.Contains("GetPreviewResultSnapshotLimit()", MainWindowSource);

        string snapshotLimit = ExtractMethodWindow(MainWindowSource, "GetPreviewResultSnapshotLimit", window: 400);
        Assert.Contains("=> int.MaxValue;", snapshotLimit);
        Assert.DoesNotContain("ViewModel.IsSearching", snapshotLimit);
        Assert.DoesNotContain("MaxMatchesPerExpandChunk", snapshotLimit);

        string fullFileSection = ExtractMethodWindow(MainWindowSource, "AddFullFileSection", window: 900);
        Assert.Contains("RegisterSectionMatchTotal(block, CountContentMatchResults(target.Matches));", fullFileSection);

        string multiHighlight = ExtractMethodWindow(MainWindowSource, "ShowMultiHighlightPreviewAsync", window: 3000);
        AssertContainsInOrder(multiHighlight,
            "if (results.All(result => result.LineNumber <= 0))",
            "BuildConcatenatedSection(section, results, allLines, ViewModel.PreviewContextLines, rx: null);",
            "continue;");

        string blockSurface = ExtractMethodWindow(MainWindowSource, "ShowPreviewBlockSurface", window: 900);
        Assert.Contains("HideMatchNavPanel();", blockSurface);
    }

    [Fact]
    public void ActiveSearchPreview_BoundsDenseSingleLineInitialRender()
    {
        string buildHighlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 6000);

        AssertContainsInOrder(buildHighlight,
            "bool truncatePreviewLines = ViewModel.IsSearching",
            "? ShouldTruncateOverflowPreviewLines()",
            ": ShouldTruncatePreviewLines();");

        AssertContainsInOrder(buildHighlight,
            "if (distinctMatchLines.Length == 1)",
            "if (section.Blocks.Count - startingBlocks >= maxBlocks)",
            "AddPreviewLineParagraphsAroundResult(",
            "targetOnlyMatchEntry: true);");

        string aroundResult = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphsAroundResult", window: 3600);
        Assert.Contains("targetOnlyMatchEntry ? null : rx", aroundResult);
        Assert.Contains("minimumOne: true", aroundResult);
    }

    [Fact]
    public void FileHeaderControlClickAndDoubleClick_AreHeaderPreviewAddGestures()
    {
        Assert.Contains("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", MainWindowXaml);

        string headerCheckbox = ExtractXamlWindow("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", 500);
        Assert.Contains("IsChecked=\"{x:Bind AllSelected, Mode=OneWay}\"", headerCheckbox);
        Assert.Contains("IsThreeState=\"False\"", headerCheckbox);
        Assert.DoesNotContain("Mode=TwoWay", headerCheckbox);
        Assert.Contains("Click=\"OnFileGroupCheckBoxClicked\"", headerCheckbox);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", headerCheckbox);
        Assert.DoesNotContain("Checked=\"OnSelectAllChecked\"", headerCheckbox);
        Assert.DoesNotContain("Unchecked=\"OnSelectAllUnchecked\"", headerCheckbox);

        string checkboxSync = ExtractMethodWindow(MainWindowSource, "SetFileGroupCheckBoxState");
        Assert.Contains("checkBox.IsThreeState = false;", checkboxSync);
        Assert.Contains("checkBox.IsChecked = desired;", checkboxSync);
        Assert.Contains("VisualStateManager.GoToState(checkBox, desired ? \"Checked\" : \"Unchecked\", false)", checkboxSync);

        string headerGrid = ExtractXamlWindow("PointerPressed=\"OnFileGroupHeaderPointerPressed\"", 260);
        Assert.Contains("PointerReleased=\"OnFileGroupHeaderPointerReleased\"", headerGrid);
        Assert.DoesNotContain("Tapped=\"OnFileGroupHeaderTapped\"", headerGrid);
        Assert.Contains("DoubleTapped=\"OnFileGroupHeaderDoubleTapped\"", headerGrid);

        string headerLayout = ExtractXamlWindow("DoubleTapped=\"OnFileGroupHeaderDoubleTapped\"", 4000);
        // Filename column is a fixed width so directory paths line up vertically across
        // every file row; the directory column takes the remaining * width.
        AssertContainsInOrder(headerLayout,
            "<ColumnDefinition Width=\"Auto\" />",
            "<ColumnDefinition Width=\"Auto\" />",
            "<ColumnDefinition Width=\"320\" MinWidth=\"0\" />",
            "<ColumnDefinition Width=\"*\" />",
            "<ColumnDefinition Width=\"Auto\" />");
        string wideDirectory = ExtractXamlWindow("Grid.Column=\"3\" Text=\"{x:Bind DirectoryName}\"", 900);
        Assert.Contains("Tag=\"WideDir\"", wideDirectory);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", wideDirectory);
        Assert.Contains("Grid.Column=\"3\"", wideDirectory);
        Assert.Contains("private const double ResultsCompactThreshold = 760;", MainWindowSource);
        Assert.DoesNotContain("<ColumnDefinition Width=\"Auto\" MaxWidth=\"650\" />", headerLayout);
        Assert.DoesNotContain("private const double ResultsCompactThreshold = 550;", MainWindowSource);

        string pointerPressed = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderPointerPressed", window: 1800);
        Assert.Contains("_ctrlFileHeaderGestureWasExpanded = group.IsExpanded;", pointerPressed);
        Assert.Contains("e.Handled = true;", pointerPressed);

        string pointerReleased = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderPointerReleased", window: 2200);
        AssertContainsInOrder(pointerReleased,
            "bool wasExpanded = _ctrlFileHeaderGestureWasExpanded;",
            "ClearCtrlFileHeaderGesture();",
            "await SelectFileGroupMatchesAndPreviewAsync(group, \"ctrl click\", preserveExpansionState: wasExpanded);");

        Assert.DoesNotContain("Tapped=\"OnFileGroupHeaderTapped\"", MainWindowXaml);

        string doubleTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderDoubleTapped");
        AssertContainsInOrder(doubleTap,
            "if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))",
            "return;",
            "e.Handled = true;",
            "await SelectFileGroupMatchesAndPreviewAsync(g, \"double click\");");

        string selectAndPreview = ExtractMethodWindow(MainWindowSource, "SelectFileGroupMatchesAndPreviewAsync");
        Assert.Contains("SelectFileGroupMatches(group);", selectAndPreview);
        Assert.Contains("_initialMatchScrolled = false;", selectAndPreview);
        Assert.Contains("var results = GetPreviewableResults(group);", selectAndPreview);
        Assert.DoesNotContain("RecordCtrlFileHeaderPreview", MainWindowSource);
        Assert.DoesNotContain("WasCtrlFileHeaderPreviewJustHandled", MainWindowSource);
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
    public void PlainResultClicks_DoNotAddFilesToPreviewPanel()
    {
        string itemClick = ExtractMethodWindow(MainWindowSource, "OnResultItemClick", window: 450);
        Assert.Contains("OnResultItemClick: no preview change", itemClick);
        Assert.DoesNotContain("UpdatePreviewAsync(g[0])", itemClick);
        Assert.DoesNotContain("TryScrollToPreviewSection(g[0].FilePath)", itemClick);
        Assert.DoesNotContain("OnFileGroupHeaderTapped", MainWindowSource);
        Assert.DoesNotContain("SelectFileGroupMatchesAndPreviewAsync(g, \"single click\")", MainWindowSource);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped", window: 900);
        Assert.Contains("OnMatchLineTapped: no preview change", tapped);
        Assert.DoesNotContain("UpdatePreviewAsync", tapped);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", tapped);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", tapped);
        Assert.DoesNotContain("EnsureCheckedMatchInPreviewAsync", tapped);
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
            "groupsToPreview.Add(ViewModel.ResultGroups[i]);",
            "else",
            "ViewModel.ResultGroups[i].DeselectAll();",
            "else if (shouldSelect)",
            "group.SelectAll();",
            "groupsToPreview.Add(group);",
            "else",
            "group.DeselectAll();");
        Assert.Contains("SetFileGroupCheckBoxState(checkBox, group.AllSelected);", checkboxClicked);
        Assert.Contains("await EnsureFileGroupsInPreviewAsync(groupsToPreview, group.FilePath);", checkboxClicked);

        string ensureGroups = ExtractMethodWindow(MainWindowSource, "EnsureFileGroupsInPreviewAsync");
        Assert.Contains("var selectedResults = GetPreviewableResults(fileGroup);", ensureGroups);
        Assert.Contains("await PrependPreviewSectionsForFilesAsync(newFiles, scrollToFile);", ensureGroups);
    }

    [Fact]
    public void FileHeaderContextMenu_PreviewsRightClickedGroupWhenNothingIsChecked()
    {
        string headerFlyout = ExtractXamlWindow("<MenuFlyout Opening=\"OnFileHeaderContextMenuOpening\"", 900);
        string fileHeaderGrid = ExtractXamlWindow("PointerPressed=\"OnFileGroupHeaderPointerPressed\"", 1400);
        string resultsList = ExtractXamlWindow("x:Name=\"ResultsList\"", 1200);
        Assert.Contains("PointerPressed=\"OnResultsListPointerPressed\"", resultsList);
        Assert.Contains("RightTapped=\"OnResultsListRightTapped\"", resultsList);
        Assert.Contains("Tag=\"{x:Bind FilePath}\"", fileHeaderGrid);
        Assert.Contains("Background=\"Transparent\"", fileHeaderGrid);
        Assert.Contains("<Grid.ContextFlyout>", fileHeaderGrid);
        Assert.Contains("Text=\"Preview all selected\"", headerFlyout);
        Assert.Contains("Click=\"OnPreviewSelectedFiles\"", headerFlyout);
        Assert.Contains("Tag=\"{x:Bind FilePath}\"", headerFlyout);

        string opening = ExtractMethodWindow(MainWindowSource, "OnFileHeaderContextMenuOpening", window: 1800);
        AssertContainsInOrder(opening,
            "var contextGroup = GetFileHeaderContextGroup(flyout)",
            "flyout.Items.OfType<MenuFlyoutItem>()",
            "int checkedCount = GetCheckedFileGroups().Count;",
            "previewAllItem.Text = $\"Preview all selected ({checkedCount})\";",
            "previewAllItem.Tag = contextGroup;",
            "previewAllItem.Visibility = checkedCount > 1",
            "int count = checkedCount > 0 ? checkedCount : 1;");

        string contextGroup = ExtractMethodWindow(MainWindowSource, "GetFileHeaderContextGroup", window: 1400);
        Assert.Contains("if (sender is MenuFlyout { Target: FrameworkElement target })", contextGroup);
        Assert.Contains("var taggedTargetGroup = GetFileHeaderContextGroup(target);", contextGroup);

        string resultsOpening = ExtractMethodWindow(MainWindowSource, "OnResultsContextMenuOpening", window: 1400);
        AssertContainsInOrder(resultsOpening,
            "var checkedGroups = GetCheckedFileGroups();",
            "var contextGroup = checkedGroups.Count == 0 ? GetRecentResultsContextMenuGroup() : null;",
            "int checkedCount = checkedGroups.Count;",
            "CtxPreviewSelected.Text = $\"Preview all selected ({checkedCount})\";",
            "CtxPreviewSelected.Tag = contextGroup;",
            "CtxPreviewSelected.Visibility = checkedCount > 1",
            "int count = checkedCount > 0 ? checkedCount : contextGroup is null ? 0 : 1;");

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
        AssertContainsInOrder(highlight,
            "distinctMatchLines.Length == 1",
            "bool isMatchLine = lineNum == matchLineNumber;",
            "truncate: truncatePreviewLines",
            "fall through to AppendHighlightMatchWindows");
        Assert.Contains("bool truncatePreviewLines = ShouldTruncatePreviewLines()", highlight);
        string previewLineTruncation = ExtractMethodWindow(MainWindowSource, "ShouldTruncatePreviewLines", window: 400);
        Assert.Contains("PreviewWrapMode.NoWrap", previewLineTruncation);
        Assert.DoesNotContain("allLines is { Length: 1 }", MainWindowSource);
        Assert.DoesNotContain("_singleLineSections", MainWindowSource);

        string multiHighlight = ExtractMethodWindow(MainWindowSource, "ShowMultiHighlightPreviewAsync");
        Assert.Contains("actualMatchEntries = _matchParagraphs.Count - sectionMatchStart", multiHighlight);
        Assert.Contains("renderedCount > actualMatchEntries", multiHighlight);
        Assert.Contains("renderedCount = actualMatchEntries", multiHighlight);

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection");
        Assert.Contains("int maxMatches = EffectiveInitialMaxMatchesPerSection", concatenated);
        Assert.Contains("cap = Math.Min(results.Count, maxMatches)", concatenated);
        Assert.Contains("int maxBlocks = EffectiveInitialMaxPreviewBlocksPerSection", concatenated);
        Assert.Contains("section.Blocks.Count - startingBlocks >= maxBlocks", concatenated);
        Assert.Contains("remainingResults: results.GetRange(renderedResults, results.Count - renderedResults)", concatenated);
        Assert.Contains("RegisterSectionOverflow", concatenated);

        string appendWindows = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows");
        Assert.Contains("int maxAdditionalBlocks", appendWindows);
        Assert.Contains("paragraphsAdded < maxAdditionalBlocks", appendWindows);
        Assert.Contains("BuildMatchByLineForRanges", appendWindows);
        Assert.Contains("CountPrefixResultsThroughLine", appendWindows);
        Assert.Contains("CapConsumedResultsToVisibleEntriesForTruncatedWindow", appendWindows);
        Assert.Contains("AddPreviewLineParagraphsAroundResult", appendWindows);
        Assert.Contains("continuationGutter: true", appendWindows);
        Assert.Contains("if (!truncatePreviewLines)", appendWindows);
        AssertContainsInOrder(appendWindows,
            "consumedResults = CountPrefixResultsThroughLine",
            "consumedResults = CapConsumedResultsToVisibleEntriesForTruncatedWindow");

        string overflowTruncation = ExtractMethodWindow(MainWindowSource, "ShouldTruncateOverflowPreviewLines", window: 900);
        Assert.Contains("ShouldTruncatePreviewLines()", overflowTruncation);
        Assert.Contains("PreviewWrapMode.NoWrap", overflowTruncation);

        string aroundResult = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphsAroundResult");
        Assert.Contains("bool truncate = true", aroundResult);
        Assert.Contains("firstParagraph is not null || continuationGutter", aroundResult);
        Assert.Contains("ScheduleGutterSync(section);", aroundResult);

        string addParagraphs = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphs");
        Assert.Contains("ScheduleGutterSync(section);", addParagraphs);

        string getPreviewLines = ExtractMethodWindow(MainWindowSource, "GetPreviewLines");
        Assert.Contains("lines.Add((allLines[i], i + 1))", getPreviewLines);
        Assert.DoesNotContain("r.MatchLine : allLines[i]", getPreviewLines);

        string expandChunk = ExtractMethodWindow(MainWindowSource, "ExpandSectionNextChunk");
        Assert.Contains("MaxPreviewBlocksPerExpandChunk", expandChunk);
        Assert.Contains("bool truncatePreviewLines = ShouldTruncateOverflowPreviewLines()", expandChunk);
        Assert.Contains("truncatePreviewLines && consumed == 0", expandChunk);
        Assert.Contains("int? maxResultsToExpand = null", expandChunk);
        Assert.Contains("requestedChunkSize", expandChunk);

        string expandScrollChunk = ExtractMethodWindow(MainWindowSource, "ExpandOverflowChunk");
        Assert.Contains("bool truncatePreviewLines = ShouldTruncateOverflowPreviewLines()", expandScrollChunk);

        string autoOverflow = ExtractMethodWindow(MainWindowSource, "TryAutoLoadOverflowOnScroll", window: 2200);
        Assert.Contains("IsOverflowAutoLoadSuppressedForMatchNavigation()", autoOverflow);

        string scrollAfterMatch = ExtractMethodWindow(MainWindowSource, "ScrollAfterMatchNavigation", window: 1200);
        Assert.Contains("SuppressOverflowAutoLoadForMatchNavigation();", scrollAfterMatch);

        string changePreviewView = ExtractMethodWindow(MainWindowSource, "ChangePreviewViewForMatchNavigation", window: 700);
        AssertContainsInOrder(changePreviewView,
            "SuppressOverflowAutoLoadForMatchNavigation();",
            "PreviewScrollViewer.ChangeView");

        Assert.Contains("actualCenterAccepted = ChangePreviewViewForMatchNavigation", MainWindowSource);
        Assert.Contains("bool accepted = ChangePreviewViewForMatchNavigation(null, correctiveTarget)", MainWindowSource);
        Assert.DoesNotContain("actualCenterAccepted = PreviewScrollViewer.ChangeView", MainWindowSource);

        string goToNext = ExtractMethodWindow(MainWindowSource, "GoToNextMatchAsync", window: 3600);
        Assert.Contains("ExpandSectionNextChunk(curBlock, SingleStepOverflowExpandMatches)", goToNext);
        Assert.Contains("ScrollAfterMatchNavigation(block, para, justMaterialized || expandedOverflow", goToNext);

        string sectionNext = ExtractMethodWindow(MainWindowSource, "OnSectionNextMatch", window: 1800);
        Assert.Contains("ScrollAfterMatchNavigation(sn.Block, para, justMaterialized: expandedOverflow", sectionNext);
    }

    [Fact]
    public void GlobalMatchPaginator_UsesKnownTotalsWithoutRenderedOverflowRatchet()
    {
        string knownTotal = ExtractMethodWindow(MainWindowSource, "GetKnownPreviewMatchTotal", window: 1400);
        Assert.Contains("_sectionTotalMatchCounts.Count + deferredFiles >= _previewTotalFileCount", knownTotal);
        Assert.Contains("_sectionTotalMatchCounts.Values.Sum() + deferredMatches", knownTotal);
        Assert.Contains("return _previewTotalMatchCount;", knownTotal);

        string stableTotal = ExtractMethodWindow(MainWindowSource, "GetStableMatchNavTotal", window: 1800);
        AssertContainsInOrder(stableTotal,
            "int renderedTotal = GetRenderedMatchTotal();",
            "int knownTotal = GetKnownPreviewMatchTotal();",
            "if (knownTotal > 0)",
            "return Math.Max(knownTotal, _matchParagraphs.Count);");
        int knownBranchStart = stableTotal.IndexOf("if (knownTotal > 0)", StringComparison.Ordinal);
        int fallbackStart = stableTotal.IndexOf("int stableTotal", StringComparison.Ordinal);
        Assert.True(knownBranchStart >= 0 && fallbackStart > knownBranchStart);
        string knownBranch = stableTotal[knownBranchStart..fallbackStart];
        Assert.DoesNotContain("stableTotal", knownBranch);
    }

    [Fact]
    public void SectionMatchPaginator_UsesRegisteredTotalsWithoutRenderedOverflowRatchet()
    {
        string sectionTotal = ExtractMethodWindow(MainWindowSource, "GetSectionMatchTotal", window: 1200);
        AssertContainsInOrder(sectionTotal,
            "int renderedTotal = sectionNav.Matches.Count;",
            "if (_sectionOverflow.TryGetValue(sectionNav.Block, out var ov))",
            "renderedTotal += CountOverflowRemainingMatches(ov);",
            "if (_sectionTotalMatchCounts.TryGetValue(sectionNav.Block, out int total))",
            "return total;",
            "return renderedTotal;");
        Assert.DoesNotContain("Math.Max(total, renderedTotal)", sectionTotal);

        string overlay = ExtractMethodWindow(MainWindowSource, "UpdateSectionNavOverlay", window: 1000);
        AssertContainsInOrder(overlay,
            "int total = GetSectionMatchTotal(_activeSectionNav);",
            "int displayIndex = Math.Clamp(_activeSectionNav.CurrentIndex + 1, 1, total);",
            "string matchWord = total == 1 ? \"match\" : \"matches\";",
            "SectionNavLabel.Text = $\"Occurrence {displayIndex:N0}/{total:N0} ({total:N0} {matchWord} in file)\";");

        string mainWindowXaml = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        Assert.Contains("Current occurrence and total matches in the active preview file.", mainWindowXaml);
    }

    [Fact]
    public void LargeLineMatchWindows_ResolveDisplayColumnsBackToSourceColumns()
    {
        string truncateAroundResult = ExtractMethodWindow(MainWindowSource, "TruncatePreviewLineAroundResult");
        Assert.Contains("ResolveSourceMatchStart(line, result, rx)", truncateAroundResult);

        string resolver = ExtractMethodWindow(MainWindowSource, "ResolveSourceMatchStart", window: 2400);
        Assert.Contains("int candidate = result.SourceMatchStartColumn;", resolver);
        Assert.Contains("result.MatchLine", resolver);
        Assert.Contains("displayLine.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)", resolver);
        Assert.Contains("displayLine.EndsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)", resolver);
        AssertContainsInOrder(resolver,
            "int candidate = result.SourceMatchStartColumn;",
            "candidate = result.MatchStartColumn;");
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
    public void PreviewTruncationEllipses_AreBlueClickableShowMoreControls()
    {
        string previewBuilder = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewBuilder.cs"));
        string selectionAutoScroll = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewSelectionAutoScroll.cs"));

        Assert.Contains("private sealed class PreviewTruncatedLineState", previewBuilder);
        Assert.Contains("private sealed record PreviewShowMoreAction", previewBuilder);
        Assert.Contains("s_previewShowMoreEllipsisBrush = new(Microsoft.UI.Colors.DodgerBlue)", previewBuilder);
        AssertContainsInOrder(previewBuilder,
            "private int GetPreviewShowMoreMaxWindowLength()",
            "GetEffectiveSegmentSize() - (2 * LineTruncator.Ellipsis.Length)",
            "private int GetPreviewTruncatedLength()",
            "return Math.Min(configuredLength, GetPreviewShowMoreMaxWindowLength());");
        Assert.Contains("LogPreviewShowMoreDiagnostics(\"BuildHighlightSection\")", previewBuilder);

        string addParagraphs = ExtractMethodWindow(previewBuilder, "AddPreviewLineParagraphs", window: 2600);
        AssertContainsInOrder(addParagraphs,
            "TruncatePreviewLineAroundResult(line, result, rx) : TruncatePreviewLineWindow(line, rx)",
            "CreatePreviewTruncatedLineState(window, line, lineNum, isMatchLine, result, rx)",
            "truncationState: isContinuation ? null : expansionState");

        string textRuns = ExtractMethodWindow(previewBuilder, "AddPreviewTextRuns", window: 2600);
        AssertContainsInOrder(textRuns,
            "line.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)",
            "CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Prefix)",
            "AddPreviewTextSpanRuns(para, line[start..end], isMatchLine, rx)",
            "CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Suffix)");

        string inline = ExtractMethodWindow(previewBuilder, "CreatePreviewShowMoreInline", window: 2600);
        Assert.Contains("private InlineUIContainer CreatePreviewShowMoreInline", inline);
        Assert.Contains("var marker = new TextBlock", inline);
        Assert.Contains("var markerHost = new Border", inline);
        Assert.Contains("Background = _transparentBrush", inline);
        Assert.Contains("Foreground = s_previewShowMoreEllipsisBrush", inline);
        Assert.Contains("markerHost.PointerEntered", inline);
        Assert.Contains("markerHost.PointerMoved", inline);
        Assert.Contains("markerHost.PointerExited", inline);
        Assert.Contains("marker.PointerEntered", inline);
        Assert.Contains("marker.PointerMoved", inline);
        Assert.Contains("marker.PointerExited", inline);
        Assert.Contains("CancelPreviewShowMoreTooltipHide()", inline);
        Assert.Contains("QueuePreviewShowMoreTooltipHide()", inline);
        Assert.Contains("markerHost.Tapped", inline);
        Assert.Contains("ExpandPreviewShowMoreFromInlineClick(action)", inline);
        Assert.DoesNotContain("ShowPreviewShowMoreTooltip(action, e.GetPosition", inline);
        Assert.Contains("_previewShowMoreMarkerCount++", inline);
        Assert.Contains("var container = new InlineUIContainer", inline);
        Assert.Contains("s_previewShowMoreActions.Add(markerHost, action);", inline);
        Assert.Contains("s_previewShowMoreActions.Add(container, action);", inline);
        Assert.Contains("s_previewShowMoreActions.Add(marker, action);", inline);
        Assert.DoesNotContain("ToolTipService.SetToolTip", inline);

        string inlineClick = ExtractMethodWindow(previewBuilder, "ExpandPreviewShowMoreFromInlineClick", window: 1200);
        Assert.Contains("CancelPreviewSelectionAutoScrollForShowMore(\"show-more-inline-click\")", inlineClick);
        Assert.Contains("HidePreviewShowMoreTooltip()", inlineClick);
        Assert.Contains("QueuePreviewTruncatedLineExpansion(action, PreviewShowMoreExpandMode.More)", inlineClick);

        string queueMethod = ExtractMethodWindow(previewBuilder, "QueuePreviewTruncatedLineExpansion", window: 2200);
        Assert.Contains("DispatcherQueue.TryEnqueue", queueMethod);
        Assert.Contains("DispatcherQueuePriority.Normal", queueMethod);
        Assert.DoesNotContain("DispatcherQueuePriority.Low", queueMethod);
        Assert.Contains("PreviewShowMoreExpandMode mode", queueMethod);
        Assert.Contains("ExpandPreviewTruncatedLineSafely(action, mode)", queueMethod);

        string safeMethod = ExtractMethodWindow(previewBuilder, "ExpandPreviewTruncatedLineSafely", window: 1600);
        Assert.Contains("try", safeMethod);
        Assert.Contains("Stopwatch.StartNew()", safeMethod);
        Assert.Contains("expand complete", safeMethod);
        Assert.Contains("LogService.Instance.Critical", safeMethod);

        Assert.Contains("x:Name=\"PreviewShowMoreTooltipOverlay\"", MainWindowXaml);
        Assert.Contains("Visibility=\"Collapsed\"", MainWindowXaml);
        Assert.Contains("x:Name=\"PreviewShowMoreTooltipBubble\"", MainWindowXaml);
        Assert.Contains("Background=\"#B8181818\"", MainWindowXaml);
        Assert.Contains("x:Name=\"PreviewShowMoreTooltipContent\"", MainWindowXaml);
        Assert.Contains("PreviewShowMoreTooltipCursorGapDip = 12", previewBuilder);
        Assert.Contains("PreviewShowMoreTooltipLeftShiftRatio = 0.10", previewBuilder);

        string tooltip = ExtractMethodWindow(previewBuilder, "ShowPreviewShowMoreTooltip", window: 3600);
        Assert.Contains("PreviewShowMoreTooltipContent.Children.Clear()", tooltip);
        Assert.Contains("Segoe MDL2 Assets", tooltip);
        Assert.Contains("\\uE72B", tooltip);
        AssertContainsInOrder(tooltip,
            "edge == PreviewShowMoreEdge.Prefix",
            "CreatePreviewShowMoreActionButton(\"\\uE72B\", \"Show more\"",
            "CreatePreviewShowMoreActionButton(\"\\uE72B\", \"Show all\"");
        AssertContainsInOrder(tooltip,
            "else",
            "CreatePreviewShowMoreActionButton(\"\\uE72A\", \"Show more\"",
            "CreatePreviewShowMoreActionButton(\"\\uE72A\", \"Show all\"");
        Assert.Contains("\\uE72A", tooltip);
        Assert.Contains("Canvas.SetLeft", tooltip);
        Assert.Contains("Canvas.SetTop", tooltip);
        Assert.Contains("pointer.X + PreviewShowMoreTooltipCursorGapDip - (bubbleWidth * PreviewShowMoreTooltipLeftShiftRatio)", tooltip);
        Assert.Contains("pointer.Y + PreviewShowMoreTooltipCursorGapDip", tooltip);
        Assert.Contains("CancelPreviewSelectionAutoScrollForShowMore(\"show-more-tooltip\")", tooltip);
        Assert.Contains("EnsurePreviewShowMoreTooltipHandlers()", tooltip);
        Assert.Contains("CancelPreviewShowMoreTooltipHide()", tooltip);

        string actionButton = ExtractMethodWindow(previewBuilder, "CreatePreviewShowMoreActionButton", window: 3600);
        Assert.Contains("Button", actionButton);
        Assert.Contains("button.PointerEntered", actionButton);
        Assert.Contains("button.PointerMoved", actionButton);
        Assert.Contains("button.PointerExited", actionButton);
        Assert.Contains("_previewShowMorePointerOverPanel = true", actionButton);
        Assert.Contains("CancelPreviewShowMoreTooltipHide()", actionButton);
        Assert.Contains("CancelPreviewSelectionAutoScrollForShowMore($\"show-more-{mode}\")", actionButton);
        Assert.Contains("QueuePreviewTruncatedLineExpansion(action, mode)", actionButton);
        Assert.DoesNotContain("HidePreviewShowMoreTooltip()", actionButton);

        string hitTest = ExtractMethodWindow(previewBuilder, "TryGetPreviewShowMoreActionFromPointer", window: 2200);
        Assert.Contains("TryGetPreviewShowMoreAction(e.OriginalSource, out action)", hitTest);
        Assert.Contains("VisualTreeHelper.FindElementsInHostCoordinates(point, block)", hitTest);
        Assert.Contains("LogService.Instance.Verbose(\"PreviewShowMore\"", hitTest);

        string delayedHide = ExtractMethodWindow(previewBuilder, "QueuePreviewShowMoreTooltipHide", window: 2400);
        Assert.Contains("PreviewShowMoreTooltipHideDelayMs", delayedHide);
        Assert.Contains("DispatcherQueue.CreateTimer()", delayedHide);
        Assert.Contains("_previewShowMorePointerOverMarker || _previewShowMorePointerOverPanel", delayedHide);
        Assert.Contains("HidePreviewShowMoreTooltip()", delayedHide);

        string handlers = ExtractMethodWindow(previewBuilder, "EnsurePreviewShowMoreTooltipHandlers", window: 1800);
        Assert.Contains("PreviewShowMoreTooltipOverlay.PointerPressed += OnPreviewShowMoreTooltipOverlayPointerPressed", handlers);

        string overlayPointerPressed = ExtractMethodWindow(previewBuilder, "OnPreviewShowMoreTooltipOverlayPointerPressed", window: 1800);
        Assert.Contains("IsElementWithin(source, PreviewShowMoreTooltipBubble)", overlayPointerPressed);
        Assert.Contains("HidePreviewShowMoreTooltip()", overlayPointerPressed);
        Assert.Contains("e.Handled = true", overlayPointerPressed);

        string contentPointerPressed = ExtractMethodWindow(previewBuilder, "HidePreviewShowMoreTooltipForContentPointer", window: 900);
        Assert.Contains("PreviewShowMoreTooltipOverlay.Visibility == Visibility.Visible", contentPointerPressed);
        Assert.Contains("HidePreviewShowMoreTooltip()", contentPointerPressed);

        string expand = ExtractMethodWindow(previewBuilder, "ExpandPreviewTruncatedLine", window: 3600);
        AssertContainsInOrder(expand,
            "mode == PreviewShowMoreExpandMode.All",
            "newStart = 0",
            "newEnd = lineLength",
            "action.Edge == PreviewShowMoreEdge.Prefix",
            "newStart = state.SourceStart - add",
            "action.Edge == PreviewShowMoreEdge.Suffix",
            "newEnd = state.SourceEnd + add",
            "var scrollSnapshot = CapturePreviewShowMoreScrollPosition(action)",
            "RebuildPreviewTruncatedLineParagraph(action.Paragraph, state)");
        Assert.Contains("RestorePreviewShowMoreScrollPosition(scrollSnapshot)", expand);

        string captureScroll = ExtractMethodWindow(previewBuilder, "CapturePreviewShowMoreScrollPosition", window: 1800);
        Assert.Contains("PreviewScrollViewer.HorizontalOffset", captureScroll);
        Assert.Contains("PreviewScrollViewer.VerticalOffset", captureScroll);
        Assert.Contains("sectionScroller?.HorizontalOffset", captureScroll);

        string restoreScroll = ExtractMethodWindow(previewBuilder, "RestorePreviewShowMoreScrollPosition", window: 2200);
        Assert.Contains("ApplyPreviewShowMoreScrollPosition(snapshot)", restoreScroll);
        Assert.Contains("DispatcherQueue.TryEnqueue", restoreScroll);
        Assert.Contains("PreviewScrollViewer.UpdateLayout()", restoreScroll);
        Assert.Contains("snapshot.SectionScroller?.UpdateLayout()", restoreScroll);

        string applyScroll = ExtractMethodWindow(previewBuilder, "ApplyPreviewShowMoreScrollPosition", window: 1600);
        Assert.Contains("RestoreScrollViewerOffsets(", applyScroll);
        Assert.Contains("snapshot.SectionScroller", applyScroll);

        string rebuild = ExtractMethodWindow(previewBuilder, "RebuildPreviewTruncatedLineParagraph", window: 2200);
        Assert.Contains("AddPreviewFullLineSegmentRuns(paragraph, state)", rebuild);
        string fullLine = ExtractMethodWindow(previewBuilder, "AddPreviewFullLineSegmentRuns", window: 2200);
        Assert.Contains("paragraph.Inlines.Add(new LineBreak())", fullLine);
        Assert.Contains("AddPreviewTextSpanRuns", fullLine);

        Assert.Contains("IsPreviewShowMorePointerSource(e.OriginalSource)", selectionAutoScroll);
        Assert.Contains("CancelPreviewSelectionAutoScrollForShowMore(\"show-more-pointer-pressed\")", selectionAutoScroll);
        Assert.Contains("StopPreviewSelectionAutoScrollTimer(\"scroll-boundary-noop\")", selectionAutoScroll);
        Assert.Contains("(!isNoOp && !accepted)", selectionAutoScroll);
        Assert.Contains("HidePreviewShowMoreTooltipForContentPointer()", selectionAutoScroll);
        Assert.Contains("OnPreviewShowMorePointerMoved", selectionAutoScroll);
        Assert.Contains("TryGetPreviewShowMoreAction(originalSource, out _)", selectionAutoScroll);
        Assert.Contains("s_previewShowMoreActions.TryGetValue(current, out var value)", selectionAutoScroll);
        Assert.Contains("catch (ArgumentException)", selectionAutoScroll);
    }

    [Fact]
    public void WrappedPreviewGutter_RepeatsSeparatorForContinuationRows()
    {
        string sync = ExtractMethodWindow(MainWindowSource, "SyncGutterParagraphHeights", window: 2600);
        AssertContainsInOrder(sync,
            "int visualLineCount = Math.Max(1",
            "SetGutterWrappedContinuationRows(gp, visualLineCount);",
            "targetBottom = cp.Margin.Bottom + Math.Max(0, contentHeight - visualLineCount * lineHeight)",
            "SetGutterWrappedContinuationRows(gp, visualLineCount: 1)");

        string helper = ExtractMethodWindow(MainWindowSource, "SetGutterWrappedContinuationRows", window: 2200);
        Assert.Contains("new LineBreak()", helper);
        Assert.Contains("Text = \"       │ \"", helper);
        Assert.Contains("Foreground = s_gutterSepBrush", helper);
        Assert.Contains("s_gutterWrappedContinuationCounts.Remove(gutterParagraph);", helper);
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
        Assert.Contains("hit.Foreground = _matchTextBrush;", makeParagraph);
        Assert.Contains("if (!isMatchLine) before.Foreground = s_contextTextBrush;", makeParagraph);
        Assert.Contains("if (!isMatchLine) tail.Foreground = s_contextTextBrush;", makeParagraph);

        string addParagraphs = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphs");
        Assert.Contains("if (!isMatchLine || matchParagraphs is null)", addParagraphs);
        Assert.Contains("AddMatchEntries(", addParagraphs);
    }

    [Fact]
    public void LineCheckboxClick_AddsCheckedMatchContextToPreview()
    {
        Assert.Contains("Click=\"OnMatchLineCheckBoxClicked\"", MainWindowXaml);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped");
        Assert.Contains("e.OriginalSource is DependencyObject source && IsInsideButton(source)", tapped);
        Assert.Contains("OnMatchLineTapped: no preview change", tapped);

        string checkboxClicked = ExtractMethodWindow(MainWindowSource, "OnMatchLineCheckBoxClicked", window: 1000);
        Assert.Contains("result.IsSelected = isChecked;", checkboxClicked);
        Assert.Contains("UpdateSelectionForMatchLine(result, nameof(OnMatchLineCheckBoxClicked));", checkboxClicked);
        Assert.Contains("if (result.IsSelected)", checkboxClicked);
        Assert.Contains("await EnsureCheckedMatchInPreviewAsync(result);", checkboxClicked);
        Assert.DoesNotContain("UpdatePreviewAsync", checkboxClicked);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", checkboxClicked);

        string selectionForLine = ExtractMethodWindow(MainWindowSource, "UpdateSelectionForMatchLine", window: 900);
        Assert.Contains("FindParentGroup(result)?.NotifySelectionChanged();", selectionForLine);
        Assert.Contains("ViewModel.GetAllSelectedResults();", selectionForLine);
        Assert.Contains("selection only", selectionForLine);
        Assert.DoesNotContain("UpdatePreviewAsync", selectionForLine);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", selectionForLine);

        string ensureChecked = ExtractMethodWindow(MainWindowSource, "EnsureCheckedMatchInPreviewAsync", window: 5000);
        Assert.Contains("ViewModel.HydrateResult(result);", ensureChecked);
        AssertContainsInOrder(ensureChecked,
            "if (!TryFindPreviewSection(result.FilePath, out var expander, out var section))",
            "[result.FilePath] = [result]",
            "await PrependPreviewSectionsForFilesAsync(newFiles, result.FilePath);",
            "return;");
        AssertContainsInOrder(ensureChecked,
            "await MaterializeLazySectionAsync(section);",
            "if (!TryFindPreviewMatchParagraph(section, result, out var paragraph, out var matchInPara))",
            "await AppendCheckedMatchContextAsync(section, result);",
            "ReorderMatchParagraphsToPreviewSectionOrder();");
        Assert.Contains("SetCurrentMatchToMatch(section, paragraph, matchInPara);", ensureChecked);
        Assert.Contains("ScrollPreviewToLine(section, paragraph);", ensureChecked);

        string appendContext = ExtractMethodWindow(MainWindowSource, "AppendCheckedMatchContextAsync", window: 5000);
        Assert.Contains("int previewLines = ViewModel.PreviewContextLines;", appendContext);
        Assert.Contains("ReadAllLinesWithEncodingSync(result.FilePath)", appendContext);
        Assert.Contains("var lines = GetPreviewLines(result, allLines, previewLines, fullFile: false);", appendContext);
        Assert.Contains("bool isMatchLine = lineNum == result.LineNumber;", appendContext);
        Assert.Contains("AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, result, rx, truncate: true, _matchParagraphs, sectionNav, out _);", appendContext);
    }

    [Fact]
    public void PreviewContextChange_PreservesScrollAndAllowsUnboundedInput()
    {
        Assert.Contains("Value=\"{x:Bind ViewModel.PreviewContextLines, Mode=TwoWay}\" Minimum=\"0\"", MainWindowXaml);
        Assert.DoesNotContain("ViewModel.PreviewContextLines, Mode=TwoWay}\" Minimum=\"0\" Maximum", MainWindowXaml);
        Assert.Contains("var prevCtx = new NumberBox { Value = _viewModel.PreviewContextLines, Minimum = 0 };", SettingsWindowSource);
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
    public void GoToLine_CentersPreviewUsingActualParagraphCoordinatesBeforeEstimates()
    {
        string goToLine = ExtractMethodWindow(MainWindowSource, "ShowGoToLineDialogForPreviewAsync", window: 1800);
        Assert.Contains("ScrollPreviewToLine(block, targetPara, forceCenter: true);", goToLine);

        string scroll = ExtractMethodWindow(MainWindowSource, "TryScrollPreviewToLine", window: 5200);
        AssertContainsInOrder(scroll,
            "TryGetPreviewParagraphTargetVerticalOffset(block, targetPara",
            "PreviewScrollViewer.ChangeView(null, paragraphOffset, null, disableAnimation: true)",
            "mode=actual-paragraph",
            "double lineHeight = EstimatePreviewLineHeight(block)");

        string actual = ExtractMethodWindow(MainWindowSource, "TryGetPreviewParagraphTargetVerticalOffset", window: 2800);
        AssertContainsInOrder(actual,
            "GetPreviewWrapTextWidth(block)",
            "block.Measure(new Windows.Foundation.Size(measureWidth, double.PositiveInfinity));",
            "TryGetPreviewParagraphLineRect(targetPara",
            "targetLineTop + markerHeight / 2 - viewportHeight / 2",
            "Math.Clamp(candidate, 0, PreviewScrollViewer.ScrollableHeight)");
    }

    [Fact]
    public void ActiveMatchOverlay_UsesActualOverlayCoordinatesAndWaitsForSettledScroll()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 18000);
        string transform = ExtractMethodWindow(MainWindowSource, "TransformRunRectToOverlay");
        Assert.Contains("TransformToVisual(ActiveMatchOverlay)", transform);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, rect)", updateOverlay);
        Assert.Contains("IsPreviewSectionBodySettledForActiveOverlay(block, out var layoutReason)", updateOverlay);
        Assert.Contains("ActiveMatchOverlay.Visibility = Visibility.Visible;", updateOverlay);
        Assert.Contains("block.Measure(new Windows.Foundation.Size(block.ActualWidth, double.PositiveInfinity));", updateOverlay);
        Assert.Contains("targetRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward)", updateOverlay);
        Assert.Contains("usedEndRect", updateOverlay);
        Assert.Contains("TryBuildMeasuredWrappedActiveMatchMarkerRects(", updateOverlay);
        Assert.Contains("else if (TryBuildWrappedActiveMatchMarkerRects(", updateOverlay);
        Assert.DoesNotContain("TryGetEstimatedWrappedMatchPoint", updateOverlay);
        Assert.Contains("ClampOverlayMarkerLeft", updateOverlay);
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
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 18000);

        AssertContainsInOrder(updateOverlay,
            "var point = TransformRunRectToOverlay(block, targetPara, rect);",
            "if (ViewModel.PreviewWordWrap)",
            "TryBuildMeasuredWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = measuredMarkerRects;",
            "else if (TryBuildWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = estimatedMarkerRects;");

        Assert.DoesNotContain("TryGetEstimatedWrappedMatchPoint", updateOverlay);
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
        Assert.Contains("double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;", estimate);
        Assert.Contains("double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);", estimate);
        Assert.Contains("estimatedPoint = new Windows.Foundation.Point(correctedLeft, expectedTop);", estimate);
        Assert.DoesNotContain("actualRowMatchesEstimate", estimate);
    }

    [Fact]
    public void WrappedPreviewOverlay_EstimatesWhenMeasuredXIsOffscreenEvenIfRowMatches()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 18000);
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");

        Assert.DoesNotContain("TryGetEstimatedWrappedMatchPoint", updateOverlay);
        AssertContainsInOrder(estimate,
            "double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;",
            "double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;",
            "double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);",
            "estimatedPoint = new Windows.Foundation.Point(correctedLeft, expectedTop);");
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
            "int charsPerWrappedLine = GetPreviewWrappedCharsPerLine(block, targetPara, charWidth);",
            "int startRow = column / charsPerWrappedLine;",
            "int endRow = (Math.Max(column, endExclusive - 1)) / charsPerWrappedLine;",
            "for (int row = startRow; row <= endRow; row++)",
            "markerRects.Add(new Windows.Foundation.Rect(left, top, width, markerHeight));",
            "return markerRects.Count > 1;");

        string hide = ExtractMethodWindow(MainWindowSource, "HideActiveMatchOverlay", window: 500);
        Assert.Contains("ClearActiveMatchExtraWordMarkers();", hide);
        Assert.Contains("ActiveMatchBand.Width = 0;", hide);
        Assert.Contains("ActiveMatchBand.Height = 0;", hide);
        Assert.Contains("ActiveMatchWordMarker.Width = 0;", hide);
        Assert.Contains("ActiveMatchWordMarker.Height = 0;", hide);
    }

    [Fact]
    public void ActiveOverlayHide_ClearsPreviousGeometryBeforeQueuedRetryCanShowCanvas()
    {
        string queuedUpdate = ExtractMethodWindow(MainWindowSource, "QueueActiveMatchOverlayUpdate", window: 2200);
        AssertContainsInOrder(queuedUpdate,
            "HideActiveMatchOverlay();",
            "int navIndex = _currentMatchIndex;",
            "TryUpdateActiveMatchOverlayFromActualRun(block, targetPara, targetRun, expectedVerticalOffset");

        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 2400);
        AssertContainsInOrder(updateOverlay,
            "if (ActiveMatchOverlay.Visibility != Visibility.Visible)",
            "ActiveMatchOverlay.Visibility = Visibility.Visible;",
            "return false;");

        string hide = ExtractMethodWindow(MainWindowSource, "HideActiveMatchOverlay", window: 900);
        AssertContainsInOrder(hide,
            "ClearActiveMatchExtraWordMarkers();",
            "ActiveMatchBand.Width = 0;",
            "ActiveMatchBand.Height = 0;",
            "ActiveMatchWordMarker.Width = 0;",
            "ActiveMatchWordMarker.Height = 0;",
            "ActiveMatchOverlay.Visibility = Visibility.Collapsed;");
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
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 18000);

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
            "double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;",
            "double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);",
            "estimatedPoint = new Windows.Foundation.Point(correctedLeft, expectedTop);");
        Assert.DoesNotContain("rowTolerance", estimate);
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

        string hitTest = ExtractMethodWindow(textRenderer, "GetWrappedLineHitTestYFromPointY", window: 1600);
        AssertContainsInOrder(hitTest,
            "if (IsVirtualizedWrappedLine && lineIndex == NumberOfStartLine)",
            "y,",
            "0,",
            "Math.Max(1, VirtualizedWrappedRowsToRender)");

        string lineNumbers = ExtractMethodWindow(lineNumberRenderer, "GenerateLineNumberText");
        AssertContainsInOrder(lineNumbers,
            "if (textRenderer.IsVirtualizedWrappedLine)",
            "textRenderer.WrappedStartRowOffset == 0",
            "textRenderer.VirtualizedWrappedRowsToRender",
            "return;");
    }

    private static SearchResult CreateResult(string filePath, int lineNumber) =>
        new(filePath, lineNumber, $"line {lineNumber} test", 5, 4, Array.Empty<string>(), Array.Empty<string>());

    private static string ReadMainWindowSources()
    {
        string yaguRoot = Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow");
        var sources = Directory.GetFiles(yaguRoot, "MainWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText);
        return string.Join(Environment.NewLine, sources);
    }

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
        string[] textControlBoxRoots =
        [
            Path.Combine(RepoRoot, "vendor", "TextControlBox-WinUI", "TextControlBox"),
            Path.GetFullPath(Path.Combine(RepoRoot, "..", "src", "TextControlBox-WinUI", "TextControlBox")),
        ];

        foreach (string root in textControlBoxRoots)
        {
            string path = Path.Combine(new[] { root }.Concat(pathParts).ToArray());
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        Assert.Fail($"Expected TextControlBox source file under one of: {string.Join(", ", textControlBoxRoots)}");
        return string.Empty;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}