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
        Path.Combine(RepoRoot, "Yagu", "MainWindow.PreviewEditor.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "SettingsWindow.xaml.cs"));
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
    public void LauncherMode_RetainsCardSpacingWithoutExtraWindowBottomSpace()
    {
        string searchCard = ExtractXamlWindow("<!-- Search controls card -->", 600);
        Assert.Contains("Margin=\"16,10,16,4\"", searchCard);
        Assert.Contains("Padding=\"16,12\"", searchCard);
        Assert.DoesNotContain("Margin=\"16,10,16,0\"", searchCard);
        Assert.DoesNotContain("Padding=\"16,12,16,2\"", searchCard);
        Assert.DoesNotContain("Padding=\"16,12,16,8\"", searchCard);

        string progressRow = ExtractXamlWindow("<StackPanel Grid.Row=\"3\"", 900);
        Assert.Contains("Grid.Row=\"3\"", progressRow);
        Assert.Contains("Padding=\"16,2,16,2\"", progressRow);
        Assert.Contains("Spacing=\"4\"", progressRow);
        Assert.Contains("Height=\"6\"", progressRow);
        Assert.DoesNotContain("Margin=\"0,2,0,2\"", progressRow);
        Assert.DoesNotContain("Padding=\"16,0,16,0\"", progressRow);

        string splitPane = ExtractXamlWindow("x:Name=\"SplitPaneGrid\"", 300);
        Assert.Contains("Margin=\"16,2,20,4\"", splitPane);
        Assert.DoesNotContain("Margin=\"16,0,20,4\"", splitPane);

        Assert.Contains("private const double MinimumLauncherHeightDip = 190;", MainWindowSource);
        Assert.Contains("desiredHeightDip < MinimumLauncherHeightDip", MainWindowSource);
        Assert.Contains("h < MinimumLauncherHeightDip", MainWindowSource);
        Assert.DoesNotContain("desiredHeightDip < 225", MainWindowSource);
        Assert.DoesNotContain("h < 225", MainWindowSource);
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
    public void DeveloperOptions_CanHideMemoryPressureWarningLabel()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("ShowMemoryPressureWarningLabel", settingsSource);
        Assert.Contains("ShowMemoryPressureWarningLabel { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
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
        Assert.Contains("_viewModel.ShowMemoryPressureWarningLabel = false", SettingsWindowSource);
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
    public void FileHeaderControlClickAndDoubleClick_AreHeaderPreviewAddGestures()
    {
        Assert.Contains("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", MainWindowXaml);

        string headerCheckbox = ExtractXamlWindow("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", 500);
        Assert.Contains("IsChecked=\"{x:Bind AllSelected, Mode=TwoWay}\"", headerCheckbox);
        Assert.Contains("Click=\"OnFileGroupCheckBoxClicked\"", headerCheckbox);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", headerCheckbox);
        Assert.DoesNotContain("Checked=\"OnSelectAllChecked\"", headerCheckbox);
        Assert.DoesNotContain("Unchecked=\"OnSelectAllUnchecked\"", headerCheckbox);

        string headerGrid = ExtractXamlWindow("PointerPressed=\"OnFileGroupHeaderPointerPressed\"", 260);
        Assert.Contains("PointerReleased=\"OnFileGroupHeaderPointerReleased\"", headerGrid);
        Assert.Contains("Tapped=\"OnFileGroupHeaderTapped\"", headerGrid);
        Assert.Contains("DoubleTapped=\"OnFileGroupHeaderDoubleTapped\"", headerGrid);

        string headerLayout = ExtractXamlWindow("DoubleTapped=\"OnFileGroupHeaderDoubleTapped\"", 4000);
        // Filename column is a fixed width so directory paths line up vertically across
        // every file row; the directory column takes the remaining * width.
        AssertContainsInOrder(headerLayout,
            "<ColumnDefinition Width=\"Auto\" />",
            "<ColumnDefinition Width=\"Auto\" />",
            "<ColumnDefinition Width=\"320\" />",
            "<ColumnDefinition Width=\"*\" />",
            "<ColumnDefinition Width=\"Auto\" />");
        string wideDirectory = ExtractXamlWindow("Tag=\"WideDir\"", 600);
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
    public void PlainResultClicks_DoNotAddFilesToPreviewPanel()
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
        Assert.Contains("await EnsureFileGroupsInPreviewAsync(groupsToPreview, group.FilePath);", checkboxClicked);

        string ensureGroups = ExtractMethodWindow(MainWindowSource, "EnsureFileGroupsInPreviewAsync");
        Assert.Contains("var selectedResults = fileGroup.Where(result => result.IsSelected).ToList();", ensureGroups);
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
            "truncate: true",
            "fall through to AppendHighlightMatchWindows");

        string multiHighlight = ExtractMethodWindow(MainWindowSource, "ShowMultiHighlightPreviewAsync");
        Assert.Contains("actualMatchEntries = _matchParagraphs.Count - sectionMatchStart", multiHighlight);
        Assert.Contains("renderedCount > actualMatchEntries", multiHighlight);
        Assert.Contains("renderedCount = actualMatchEntries", multiHighlight);

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection");
        Assert.Contains("cap = Math.Min(results.Count, EffectiveMaxMatchesPerSection)", concatenated);
        Assert.Contains("section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection", concatenated);
        Assert.Contains("remainingResults: results.GetRange(renderedResults, results.Count - renderedResults)", concatenated);
        Assert.Contains("RegisterSectionOverflow", concatenated);

        string appendWindows = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows");
        Assert.Contains("int maxAdditionalBlocks", appendWindows);
        Assert.Contains("paragraphsAdded < maxAdditionalBlocks", appendWindows);
        Assert.Contains("BuildMatchByLineForRanges", appendWindows);
        Assert.Contains("CountPrefixResultsThroughLine", appendWindows);
        Assert.DoesNotContain("AddPreviewLineParagraphsAroundResult", appendWindows);

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
        string yaguRoot = Path.Combine(RepoRoot, "Yagu");
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