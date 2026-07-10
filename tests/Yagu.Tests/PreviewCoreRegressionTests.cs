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
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewEditor.cs"));
    private static readonly string PreviewEditorWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "PreviewEditor", "PreviewEditorWindow.cs"));
    private static readonly string EditorPointerActionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "PointerActionsManager.cs"));
    private static readonly string EditorScrollManagerSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "ScrollManager.cs"));
    private static readonly string EditorScrollOffsetSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "IScrollOffsetSource.cs"));
    private static readonly string EditorDiagonalScrollSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "CoreTextControlBox.DiagonalScroll.cs"));
    private static readonly string EditorZoomManagerSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "ZoomManager.cs"));
    private static readonly string EditorCoreSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Core", "CoreTextControlBox.xaml.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string HelpWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Help", "HelpWindow.xaml.cs"));
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string Win32FileDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Helpers", "Win32FileDialog.cs"));
    private static readonly string AppXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "App.xaml"));
    private static readonly string TerminalHtml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Assets", "terminal.html"));

    [Fact]
    public void AppCheckBoxStyle_UsesRoundedBlueVectorTreatment()
    {
        Assert.Contains("<ResourceDictionary.ThemeDictionaries>", AppXaml);
        Assert.Contains("x:Key=\"CheckBoxCheckBackgroundFillChecked\" Color=\"#5BC9F5\"", AppXaml);
        Assert.Contains("x:Key=\"CheckBoxCheckGlyphForegroundChecked\" Color=\"#0B3141\"", AppXaml);
        Assert.Contains("x:Key=\"CheckBoxCheckBackgroundStrokeUnchecked\" Color=\"#805BC9F5\"", AppXaml);
        AssertContainsInOrder(AppXaml,
            "<Style TargetType=\"CheckBox\" BasedOn=\"{StaticResource DefaultCheckBoxStyle}\">",
            "<Setter Property=\"CornerRadius\" Value=\"10\" />");

        Assert.DoesNotContain("ImageBrush", AppXaml);
        Assert.DoesNotContain(".png", AppXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".jpg", AppXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".webp", AppXaml, StringComparison.OrdinalIgnoreCase);
    }

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
    public void BrowseDirectory_UsesWin32FolderDialogInsteadOfWinAppSdkFolderPicker()
    {
        string browse = ExtractMethodWindow(MainWindowSource, "OnBrowseDirectory", 2200);
        Assert.Contains("private async void OnBrowseDirectory", browse);
        Assert.Contains("Helpers.Win32FileDialog.SelectFolder", browse);
        Assert.Contains("ViewModel.Directory = folderPath;", browse);
        Assert.Contains("DirectoryBox.Text = folderPath;", browse);
        Assert.Contains("await ViewModel.UpdateDirectorySuggestionsForSelectedDirectoryAsync(folderPath)", browse);
        Assert.Contains("DirectoryBox.IsSuggestionListOpen = suggestionCount > 0;", browse);
        Assert.Contains("Folder browse dialog failed.", browse);
        Assert.DoesNotContain("FolderPicker", browse);
        Assert.DoesNotContain("PickSingleFolderAsync", browse);
        Assert.DoesNotContain("PickFolderAsync", MainWindowSource);

        Assert.Contains("CoCreateInstance", Win32FileDialogSource);
        Assert.Contains("Marshal.GetDelegateForFunctionPointer", Win32FileDialogSource);
        Assert.Contains("VtableSlot_GetDisplayName", Win32FileDialogSource);
        Assert.DoesNotContain("[ComImport]", Win32FileDialogSource);
        Assert.DoesNotContain("GetTypeFromCLSID", Win32FileDialogSource);
        Assert.DoesNotContain("Activator.CreateInstance", Win32FileDialogSource);
        Assert.DoesNotContain("ReleaseComObject", Win32FileDialogSource);
    }

    [Fact]
    public void ExportCommands_UseWin32SaveDialogInsteadOfWinAppSdkFileSavePicker()
    {
        // The WinAppSDK FileSavePicker routes through a broker that can silently fail to come to
        // the foreground while a search is still flooding the UI thread — the disabled owner window
        // then looks frozen and the Save dialog never appears. Export commands must use the robust
        // Win32 Common Item Dialog, which parents to the owner HWND and shows reliably in that state.
        string helper = ExtractMethodWindow(MainWindowSource, "PickExportFilePath", 1200);
        Assert.Contains("private string? PickExportFilePath(", helper);
        Assert.Contains("Helpers.Win32FileDialog.Save(hwnd, title, suggestedFileName, defaultExtension, filters)", helper);

        string exportLines = ExtractMethodWindow(MainWindowSource, "OnExportSelectedLines", 2000);
        Assert.Contains("string? path = PickExportFilePath(", exportLines);
        Assert.Contains("await File.WriteAllTextAsync(path, sb.ToString())", exportLines);
        Assert.DoesNotContain("FileSavePicker", exportLines);
        Assert.DoesNotContain("PickSaveFileAsync", exportLines);

        string exportReport = ExtractMethodWindow(MainWindowSource, "OnExportHtmlReport", 3200);
        Assert.Contains("string? path = PickExportFilePath(", exportReport);
        Assert.Contains("new FileStream(path, FileMode.Create", exportReport);
        Assert.DoesNotContain("FileSavePicker", exportReport);
        Assert.DoesNotContain("PickSaveFileAsync", exportReport);

        string textExport = ExtractMethodWindow(MainWindowSource, "PickTextExportFilePath", 400);
        Assert.Contains("private string? PickTextExportFilePath(", textExport);
        Assert.Contains("=> PickExportFilePath(", textExport);

        // No export/save path may fall back to the picker anywhere in MainWindow's save flow.
        Assert.DoesNotContain("PickTextExportFileAsync", MainWindowSource);
        Assert.DoesNotContain("FileIO.WriteTextAsync", MainWindowSource);
    }

    [Fact]
    public void LauncherMode_RetainsCardSpacingWithoutExtraWindowBottomSpace()
    {
        string searchCard = ExtractXamlWindow("<!-- Search controls card -->", 600);
        Assert.Contains("Margin=\"16,10,16,4\"", searchCard);
        Assert.Contains("Padding=\"16,18,16,6\"", searchCard);
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
        Assert.Contains("private const double DefaultSearchResultsWindowHeightDip = 900;", MainWindowSource);
        Assert.Contains("desiredHeightDip < MinimumLauncherHeightDip", MainWindowSource);
        Assert.Contains("h < MinimumLauncherHeightDip", MainWindowSource);
        Assert.Contains("DefaultSearchResultsWindowHeightDip * scale", MainWindowSource);
        Assert.DoesNotContain("desiredHeightDip < 225", MainWindowSource);
        Assert.DoesNotContain("h < 225", MainWindowSource);
        Assert.DoesNotContain("800 * scale", MainWindowSource);
    }

    [Fact]
    public void PreviewPanel_EmptyVisibleSurfaceShowsCenteredWrappedMessage()
    {
        string emptyState = ExtractXamlWindow("x:Name=\"PreviewEmptyState\"", 1800);
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

        string remove = ExtractMethodWindow(MainWindowSource, "RemovePreviewSection", 4500);
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

        // The gap between the floating search drawer and the results panel must use the
        // shared PreviewTopExpandedDrawerGap constant (= the normal stacked layout's
        // 4px card bottom margin + 2px split-pane top margin) so the gap stays fixed and
        // does not grow when the preview panel is first revealed. The old magic +10
        // made the results panel sit 4px lower in PreviewTopExpanded than in the normal
        // stacked layout.
        Assert.Contains("SearchStatusPanel.ActualHeight + PreviewTopExpandedDrawerGap;", update);
        Assert.DoesNotContain("SearchStatusPanel.ActualHeight + 10;", update);
        Assert.Contains("private const double PreviewTopExpandedDrawerGap = 6;", MainWindowSource);
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

        string singlePreview = ExtractMethodWindow(MainWindowSource, "ShowSingleFilePreviewAsync", 5600);
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

        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync", 10000);
        AssertContainsInOrder(prepend,
            "BeginPreviewContentUpdate();",
            "EnsurePreviewPanelVisible();",
            "EnsureSectionsSurface();");
        // Sections added while the editor covers the preview must be deferred as
        // lazy/collapsed so returning to the surface does not lay out many
        // expanded sections at once (multi-second freeze on "View in preview").
        AssertContainsInOrder(prepend,
            "bool editorHidingPreview = PreviewEditor.Visibility == Visibility.Visible;",
            "int eagerCount = editorHidingPreview",
            "bool expanded = !editorHidingPreview && (!bulkInsert || fileIndex < BulkExpandLimit);");
        AssertContainsInOrder(prepend,
            "PreviewSectionsPanel.Children.Insert(insertIndex++, built[i]);",
            "if (i == 0)",
            "CompletePreviewContentUpdate();");

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", 13000);
        AssertContainsInOrder(addSection,
            "PreviewSectionsPanel.Children.Add(expander);",
            "CompletePreviewContentUpdate();");
    }

    [Fact]
    public void MatchLineCheckbox_AddsAdditionalCheckedMatchesIncrementally()
    {
        // Checking an additional match line must add it to the existing preview
        // incrementally rather than tearing down and rebuilding every section.
        // Routing the additive case through UpdateMultiSelectPreviewAsync(result)
        // cleared and re-rendered all sections, which the user saw as every
        // preview flickering away and back on each newly checked match.
        string updateForSelection = ExtractMethodWindow(MainWindowSource, "UpdatePreviewForMatchSelectionAsync", window: 2200);

        AssertContainsInOrder(updateForSelection,
            "if (result.IsSelected)",
            "if (!ViewModel.MatchLineCheckAddsToPreview) return;",
            "await EnsureCheckedMatchInPreviewAsync(result);");

        // The additive branch must not rebuild the whole multi-select preview.
        Assert.DoesNotContain("await UpdateMultiSelectPreviewAsync(result);", updateForSelection);

        string updateMulti = ExtractMethodWindow(MainWindowSource, "UpdateMultiSelectPreviewAsync", 900);
        Assert.Contains("SearchResult? scrollTarget", updateMulti);
    }

    [Fact]
    public void MatchLineDeselect_DownToSingleMatch_KeepsSectionsSurface()
    {
        // Deselecting checked match lines down to a single remaining match must keep
        // the multi-section sections surface (file drawer, per-section match nav,
        // selected preview background) so the end state matches the surface produced
        // by checking a single match. Routing the count==1 deselection case to
        // ShowSingleFilePreviewAsync switched to the single-file PreviewBlock surface,
        // which dropped the file drawer and match-nav buttons and showed the per-file
        // toolbar buttons — the user saw the preview "change to an unexpected version".
        string updateForSelection = ExtractMethodWindow(MainWindowSource, "UpdatePreviewForMatchSelectionAsync", window: 2200);

        // The deselection rebuild must route a single remaining selection through the
        // sections-based multi-select preview, not the single-file block surface.
        AssertContainsInOrder(updateForSelection,
            "var remainingSelected = ViewModel.GetAllSelectedResults();",
            "if (remainingSelected.Count >= 1)",
            "await UpdateMultiSelectPreviewAsync();");

        // It must NOT fall back to the single-file PreviewBlock surface for one match.
        Assert.DoesNotContain("ShowSingleFilePreviewAsync(remainingSelected", updateForSelection);
        Assert.DoesNotContain("remainingSelected.Count == 1", updateForSelection);
    }

    [Fact]
    public void ScrollMaterializedSection_DoesNotStealSelectedBackgroundFromActiveSection()
    {
        // Scrolling a long file far enough to pull a sibling section into the
        // pre-materialization buffer must NOT change which section is "active" (the
        // one painted with the selected/black preview background). The scroll-driven
        // MaterializeVisibleLazySections sweep expands lazy sections by setting
        // IsExpanded = true, which fires the Expander.Expanding handler; that handler
        // used to unconditionally call ActivateSectionForBlock, so scrolling stole the
        // selected background from the file the user was reading. The bug only showed
        // with 2+ documents (a single document has no sibling to steal "active").

        // The scroll sweep tags each block before expanding it so the Expanding handler
        // can tell scroll-driven auto-materialization apart from a real user expansion.
        string sweep = ExtractMethodWindow(MainWindowSource, "MaterializeVisibleLazySections", 4000);
        AssertContainsInOrder(sweep,
            "if (!captured.IsExpanded)",
            "_autoMaterializingSections.Add(lazyBlock);",
            "captured.IsExpanded = true;");

        // The Expanding handler (built in AddPreviewSection) consumes the tag
        // synchronously before its first await and only activates the section when it
        // was NOT an auto-materialization (manual expand / match nav / scroll-to-section
        // still activate, because they leave the block untagged).
        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", 10000);
        AssertContainsInOrder(addSection,
            "bool autoMaterialized = _autoMaterializingSections.Remove(b);",
            "if (!autoMaterialized)",
            "ActivateSectionForBlock(b);");
    }

    [Fact]
    public void UpdateSectionMatchNavPanels_PreservesActiveSectionInMultiFileView()
    {
        // UpdateSectionMatchNavPanels runs from many scroll-driven paths (auto-load-more,
        // overflow auto-load, section materialization). In a multi-file view it must NOT
        // unconditionally null the active section — doing so deselected the file the user
        // was reading (its black "selected" background flipped to unselected on scroll).
        // It may only drop a stale active section (one removed from _sectionMatchNavs or
        // no longer holding >1 navigable matches).
        string method = ExtractMethodWindow(MainWindowSource, "UpdateSectionMatchNavPanels", 2400);
        AssertContainsInOrder(method,
            "else",
            "if (_activeSectionNav is not null",
            "_sectionMatchNavs.TryGetValue(_activeSectionNav.Block, out var cur)",
            "ReferenceEquals(cur, _activeSectionNav)",
            "GetSectionMatchTotal(_activeSectionNav) <= 1",
            "_activeSectionNav = null;");
    }

    [Fact]
    public void RefreshCurrentPreview_PreservesSelectedSectionAcrossWrapToggleRebuild()
    {
        // Toggling word wrap (to/from NoWrap) rebuilds the multi-document preview via
        // RefreshCurrentPreview(preserveScroll: true). The rebuild allocates fresh
        // SectionMatchNav/block instances, so the prior _activeSectionNav reference is
        // stale and the file the user had selected (its black "selected" background) lost
        // its selection. RefreshCurrentPreview must capture the active section's file path
        // before the rebuild and re-select it afterwards.
        string refresh = ExtractMethodWindow(MainWindowSource, "RefreshCurrentPreview", 2200);
        AssertContainsInOrder(refresh,
            "string? restoreActiveSectionFilePath = preserveScroll ? GetActiveSectionFilePath() : null;",
            "await UpdateMultiSelectPreviewAsync();",
            "RestoreActiveSectionByFilePath(restoreActiveSectionFilePath);");

        // GetActiveSectionFilePath resolves the active section's stable identity (file path).
        string getPath = ExtractMethodWindow(MainWindowSource, "GetActiveSectionFilePath", 400);
        Assert.Contains("_activeSectionNav?.Block", getPath);
        Assert.Contains("ResolvePreviewBlockFilePath(block)", getPath);

        // RestoreActiveSectionByFilePath matches the rebuilt section by file path and
        // re-activates it (no-op when empty or its section is gone).
        string restore = ExtractMethodWindow(MainWindowSource, "RestoreActiveSectionByFilePath", 700);
        AssertContainsInOrder(restore,
            "if (string.IsNullOrEmpty(filePath))",
            "foreach (var block in _sectionMatchNavs.Keys)",
            "ResolvePreviewBlockFilePath(block), filePath",
            "ActivateSectionForBlock(block);");
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
    public void PreviewSectionHeader_ActionsSitNearChevron()
    {
        string header = ExtractMethodWindow(MainWindowSource, "BuildPreviewSectionHeader", 4200);
        AssertContainsInOrder(header,
            "ColumnDefinitions =",
            "new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },",
            "new ColumnDefinition { Width = GridLength.Auto },",
            "MinWidth = 0,",
            "ColumnSpacing = 8,",
            "new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },",
            "TextTrimming = TextTrimming.CharacterEllipsis,",
            "Grid.SetColumn(infoPanel, 0);",
            "Margin = new Thickness(8, 0, 0, 0)",
            "HorizontalAlignment = HorizontalAlignment.Right,",
            "Grid.SetColumn(buttonPanel, 1);",
            "Canvas.SetZIndex(buttonPanel, 1);");
    }

    [Fact]
    public void PreviewSectionHeader_FileNameHugsFolderIconAtAnyWidth()
    {
        string header = ExtractMethodWindow(MainWindowSource, "BuildPreviewSectionHeader", 4200);

        // The file name must be left-aligned so it stays sticky to the folder
        // icon. A Stretch TextBlock constrained by MaxWidth centers inside its
        // star column, opening a gap that widens with the window.
        AssertContainsInOrder(header,
            "var fileNameText = new TextBlock",
            "MaxWidth = 360,",
            "HorizontalAlignment = HorizontalAlignment.Left,",
            "Grid.SetColumn(fileNameText, 1);");
    }

    [Fact]
    public void PreviewSectionHeader_DisablesTextOnlyActionsForImageMatches()
    {
        string header = ExtractMethodWindow(MainWindowSource, "BuildPreviewSectionHeader", 9000);

        // Image content matches (thumbnail + OCR text) are not text-editable, so the text-only
        // actions must render disabled and non-clickable. The "Show full file" and "Edit file"
        // buttons gate IsEnabled on the image check; the other actions stay enabled.
        AssertContainsInOrder(header,
            "bool isImageMatch = IsImagePreviewPath(filePath);",
            "Content = new FontIcon { Glyph = \"\\uE81E\", FontSize = 12 },",
            "IsEnabled = !isImageMatch,",
            "Content = new FontIcon { Glyph = \"\\uE70F\", FontSize = 12 },",
            "IsEnabled = !isImageMatch,");
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

        string mainWindowPropertyChanged = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 11000);
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
    public void PreviewAndEditor_SupportPinchAndCtrlWheelZoom()
    {
        // Preview: a Ctrl-modified wheel (mouse Ctrl+wheel or a precision-touchpad
        // pinch, which Windows delivers as a Ctrl-modified wheel) zooms the text
        // instead of scrolling.
        string wheel = ExtractMethodWindow(MainWindowSource, "OnPreviewPointerWheelChanged", 2600);
        AssertContainsInOrder(wheel,
            "!zoomProps.IsHorizontalMouseWheel",
            "IsPreviewZoomModifierActive(e)",
            "AdjustPreviewTextZoom(zoomProps.MouseWheelDelta);",
            "e.Handled = true;");

        // The pinch's synthesized Ctrl rides on the wheel message (e.KeyModifiers),
        // not the physical keyboard state, so both must be checked.
        string modifier = ExtractMethodWindow(MainWindowSource, "IsPreviewZoomModifierActive", 700);
        AssertContainsInOrder(modifier,
            "e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)",
            "GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)",
            "CoreVirtualKeyStates.Down");

        // Wheel deltas accumulate into whole-point font-size steps and are clamped
        // by the pure, unit-tested PreviewZoomMath helper (see PreviewZoomMathTests).
        string adjust = ExtractMethodWindow(MainWindowSource, "AdjustPreviewTextZoom", 900);
        AssertContainsInOrder(adjust,
            "int current = ResolvePreviewTextFontSize();",
            "PreviewZoomMath.ApplyWheelZoom(current, wheelDelta, ref _previewZoomWheelAccumulator)",
            "ViewModel.PreviewTextFontSize = updated;");

        // Zoom reflows the single-file block surface too (it has no separate gutter block).
        string applyFont = ExtractMethodWindow(MainWindowSource, "ApplyPreviewTextFontSettings", window: 2600);
        AssertContainsInOrder(applyFont,
            "if (PreviewBlock is not null)",
            "ApplyPreviewTextFontSettings(PreviewBlock, family, size, lineHeight);",
            "InvalidateParagraphIndexCache(PreviewBlock);");

        string blockSurface = ExtractMethodWindow(MainWindowSource, "ShowPreviewBlockSurface", 1200);
        AssertContainsInOrder(blockSurface,
            "int previewTextFontSize = ResolvePreviewTextFontSize();",
            "ApplyPreviewTextFontSettings(",
            "PreviewBlock,",
            "ResolvePreviewTextFontFamily(),");

        // Editor: the vendored control's wheel-zoom accepts the pinch modifier from the wheel message
        // (e.KeyModifiers), not just a held Ctrl key, AND accumulates sub-notch deltas via EditorZoomMath
        // so a precision-touchpad pinch (many small Ctrl-wheel deltas) actually zooms instead of the old
        // integer `delta / 20` truncating every |delta| < 20 to 0.
        string editorWheel = ExtractMethodWindow(EditorPointerActionsSource, "PointerWheelAction", 1400);
        AssertContainsInOrder(editorWheel,
            "Utils.IsKeyPressed(VirtualKey.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)",
            "zoomManager._ZoomFactor = EditorZoomMath.ApplyWheelZoom(zoomManager._ZoomFactor, delta, ref _zoomWheelAccumulator);",
            "zoomManager.UpdateZoom();");
        Assert.DoesNotContain("zoomManager._ZoomFactor += delta / 20;", EditorPointerActionsSource);
    }

    [Fact]
    public void EditorScroll_IsFunneledThroughPixelBasedOffsetSource()
    {
        // Phase 1 of PLANS/EDITOR_DIAGONAL_SCROLL_REWRITE_PLAN.md: every editor scroll-position
        // read/write goes through a single pixel-based IScrollOffsetSource seam so the Phase 2
        // backend swap (two ScrollBars -> a real ScrollViewer) is a small, contained diff.

        // The seam exists, is pixel-based, and ships a ScrollBar-backed adapter that converts
        // the legacy vertical scrollbar units internally.
        AssertContainsInOrder(EditorScrollOffsetSource,
            "internal interface IScrollOffsetSource",
            "double VerticalOffset { get; set; }",
            "double HorizontalOffset { get; set; }",
            "double VerticalExtent { get; set; }",
            "double HorizontalExtent { get; set; }",
            "double ViewportWidth { get; set; }",
            "double ViewportHeight { get; set; }",
            "void ChangeView(double? horizontalOffset, double? verticalOffset)",
            "event EventHandler ViewChanged;",
            "internal sealed class ScrollBarOffsetSource : IScrollOffsetSource");
        // The adapter delegates the pixel↔scrollbar-unit arithmetic to the unit-tested ScrollOffsetMath
        // helper (see ScrollOffsetMathTests), so the seam is pixels.
        AssertContainsInOrder(EditorScrollOffsetSource,
            "get => ScrollOffsetMath.VerticalValueToPixels(_vertical.Value, _verticalSensitivity);",
            "_vertical.Value = ScrollOffsetMath.PixelsToVerticalValue(value, _verticalSensitivity);");

        // ScrollManager owns the source and routes all offset reads/writes through it.
        Assert.Contains("public IScrollOffsetSource OffsetSource { get; private set; }", EditorScrollManagerSource);
        Assert.Contains("OffsetSource = new ScrollBarOffsetSource(this.verticalScrollBar, this.horizontalScrollBar, DefaultVerticalScrollSensitivity);", EditorScrollManagerSource);

        // The vertical scroll mutators are now pixel-based: no scroll-position write divides by
        // DefaultVerticalScrollSensitivity anymore (only the extent/Maximum math, kept for Phase 2, may).
        Assert.DoesNotContain("verticalScrollBar.Value", EditorScrollManagerSource);
        Assert.DoesNotContain("horizontalScrollBar.Value", EditorScrollManagerSource);
        string oneLineUp = ExtractMethodWindow(EditorScrollManagerSource, "ScrollOneLineUp", 300);
        Assert.Contains("OffsetSource.VerticalOffset -= textRenderer.SingleLineHeight;", oneLineUp);

        // The editor wheel handler funnels its vertical scroll through the same pixel seam.
        Assert.DoesNotContain("scrollManager.verticalScrollBar.Value", EditorPointerActionsSource);
        Assert.Contains("scrollManager.OffsetSource.VerticalOffset -= delta * scrollManager._VerticalScrollSensitivity;", EditorPointerActionsSource);
    }

    [Fact]
    public void EditorDiagonalScroll_UsesTouchpadOnlyInteractionTrackerDrivingTheOffsetSource_AndIsTornDownOnUnload()
    {
        // Option B of PLANS/EDITOR_DIAGONAL_SCROLL_REWRITE_PLAN.md: a composition InteractionTracker on the
        // selection canvas reads the raw 2-D precision-touchpad delta and drives the Phase-1 pixel seam.
        AssertContainsInOrder(EditorDiagonalScrollSource,
            "internal sealed partial class CoreTextControlBox : IInteractionTrackerOwner",
            "InteractionTracker.CreateWithOwner(compositor, this)",
            "_scrollInteractionSource = VisualInteractionSource.Create(visual);",
            // CapableTouchpadOnly leaves the wheel + Ctrl/Shift-wheel to the existing PointerWheelAction.
            "ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;",
            // ValuesChanged writes the tracker position into the same offset source the ScrollBars back.
            "public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)",
            "src.HorizontalOffset = args.Position.X;",
            "src.VerticalOffset = args.Position.Y;",
            "canvasUpdateManager.UpdateAll();");

        // The tracker is wired on Loaded and MUST be torn down on Unload (no leaked 50 ms timer / composition objects).
        Assert.Contains("Loaded += (_, _) => SetupDiagonalScroll();", EditorCoreSource);
        Assert.Contains("private void TeardownDiagonalScroll()", EditorDiagonalScrollSource);
        Assert.Contains("_scrollTrackerTimer.Stop();", EditorDiagonalScrollSource);
        Assert.Contains("_scrollTracker?.Dispose();", EditorDiagonalScrollSource);
        int stop = EditorCoreSource.IndexOf("caretBlinkManager.Stop();", StringComparison.Ordinal);
        int teardown = EditorCoreSource.IndexOf("TeardownDiagonalScroll();", StringComparison.Ordinal);
        Assert.True(stop >= 0 && teardown > stop, "Unload() must call TeardownDiagonalScroll() after caretBlinkManager.Stop().");
    }

    [Fact]
    public void EditorDiagonalScroll_ImmediatelyReSyncsTrackerToProgrammaticScrolls()
    {
        // Phase 3 of PLANS/EDITOR_DIAGONAL_SCROLL_REWRITE_PLAN.md ("reconcile every scroll consumer"):
        // a PROGRAMMATIC scroll (caret-follow, page keys, go-to-line, find reveal, match hand-off, wheel,
        // scrollbar drag) must move the InteractionTracker to the new position IMMEDIATELY, not on the next
        // 50 ms timer tick — otherwise a touchpad pan started right after snaps back to the stale position.

        // 1) The offset source now raises ViewChanged from BOTH offset setters (not just the scrollbar Scroll
        //    event), so every consumer that writes the seam notifies subscribers.
        Assert.Contains("private void OnScrollBarScroll(object sender, ScrollEventArgs e) => ViewChanged?.Invoke(this, EventArgs.Empty);", EditorScrollOffsetSource);
        AssertContainsInOrder(EditorScrollOffsetSource,
            "_vertical.Value = ScrollOffsetMath.PixelsToVerticalValue(value, _verticalSensitivity);",
            "ViewChanged?.Invoke(this, EventArgs.Empty);",
            "_horizontal.Value = ScrollOffsetMath.ClampHorizontalOffset(value);",
            "ViewChanged?.Invoke(this, EventArgs.Empty);");

        // 2) The tracker subscribes to that event on setup and re-syncs immediately, but ignores the
        //    ViewChanged its OWN touchpad write raises (flagged in ValuesChanged) so a pan can't feed back.
        AssertContainsInOrder(EditorDiagonalScrollSource,
            "offsetSource.ViewChanged += OnOffsetSourceViewChanged;",
            "private void OnOffsetSourceViewChanged(object sender, EventArgs e)",
            "if (_applyingTrackerScroll)",
            "SyncScrollTrackerToOffsetNow();",
            "internal void SyncScrollTrackerToOffsetNow()",
            "if (_scrollTrackerReady)",
            "SyncScrollTracker();");

        // 3) ValuesChanged brackets its own offset writes with the guard flag so the re-sync is skipped.
        AssertContainsInOrder(EditorDiagonalScrollSource,
            "public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)",
            "_applyingTrackerScroll = true;",
            "src.HorizontalOffset = args.Position.X;",
            "src.VerticalOffset = args.Position.Y;",
            "_applyingTrackerScroll = false;",
            "canvasUpdateManager.UpdateAll();");

        // 4) The subscription is removed on teardown so an unloaded editor can't be resurrected by the event.
        Assert.Contains("offsetSource.ViewChanged -= OnOffsetSourceViewChanged;", EditorDiagonalScrollSource);
    }

    [Fact]
    public void EditorZoom_AnchorsViewportCentreRowAcrossZoom()
    {
        // Phase 3 of PLANS/EDITOR_DIAGONAL_SCROLL_REWRITE_PLAN.md: zooming must keep the document row under
        // the vertical viewport centre stationary instead of drifting (the same raw pixel offset maps to a
        // different row once SingleLineHeight = ZoomedFontSize + LineSpacingPadding changes).

        // ZoomManager holds a ScrollManager reference so it can re-anchor the pixel offset seam.
        Assert.Contains("private ScrollManager scrollManager;", EditorZoomManagerSource);
        Assert.Contains("LineNumberRenderer lineNumberRenderer,", EditorZoomManagerSource);
        Assert.Contains("ScrollManager scrollManager", EditorZoomManagerSource);
        Assert.Contains("this.scrollManager = scrollManager;", EditorZoomManagerSource);
        Assert.Contains("zoomManager.Init(textManager, textRenderer, canvasUpdateManager, eventsManager, lineNumberRenderer, scrollManager);", EditorCoreSource);

        // UpdateZoom snapshots the OLD line height BEFORE the format rebuild, then re-anchors before redraw.
        AssertContainsInOrder(EditorZoomManagerSource,
            "float oldSingleLineHeight = textRenderer.SingleLineHeight;",
            "ZoomedFontSize = Math.Clamp(",
            "if (_ZoomFactor != OldZoomFactor)",
            "AnchorScrollAcrossZoom(oldSingleLineHeight);",
            "canvasHelper.UpdateAll();");

        // The anchor computes the new line height from ZoomedFontSize + LineSpacingPadding and keeps the
        // viewport-centre row put; horizontal scales by the font-size ratio (guarded against divide-by-zero).
        AssertContainsInOrder(EditorZoomManagerSource,
            "private void AnchorScrollAcrossZoom(float oldSingleLineHeight)",
            "IScrollOffsetSource src = scrollManager?.OffsetSource;",
            "if (src is null || oldSingleLineHeight <= 0.5f)",
            "float newSingleLineHeight = ZoomedFontSize + TextLayoutManager.LineSpacingPadding;",
            "double halfViewport = src.ViewportHeight / 2.0;",
            "double centreRow = (src.VerticalOffset + halfViewport) / oldSingleLineHeight;",
            "double newVerticalOffset = centreRow * newSingleLineHeight - halfViewport;",
            "src.VerticalOffset = newVerticalOffset < 0 ? 0 : newVerticalOffset;",
            "double fontRatio = ZoomedFontSize / oldFontSize;",
            "src.HorizontalOffset = newHorizontalOffset < 0 ? 0 : newHorizontalOffset;");
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

        string pointEntry = ExtractMethodWindow(PreviewEditorSource, "TryEnterPreviewEditorAtPointAsync", 6000);
        AssertContainsInOrder(pointEntry,
            "IsPreviewSectionBodyLaidOutForPointer(block, out string layoutReason)",
            "block.GetPositionFromPoint(point)",
            "ResolveLineNumberAtPointer(block, tp)",
            "ResolveSearchResultAtPreviewPoint(fileGroup, lineNum, clickedMatchIndex)",
            "ShowFullFileEditorAsync(target, scrollToMatch: true)");
        // The one-shot double-click gesture must NOT use the stateful overlay-centering
        // settle ladder, which deliberately returns false on first contact and would
        // silently swallow the click after a scroll.
        Assert.DoesNotContain("IsPreviewSectionBodySettledForActiveOverlay", pointEntry);
    }

    [Fact]
    public void SearchStart_CollapsesAdvancedOptions()
    {
        string buttonSearch = ExtractMethodWindow(MainWindowSource, "OnSearchCancelClick", 1700);
        AssertContainsInOrder(buttonSearch,
            "CollapseAdvancedOptionsForSearch();",
            "await SubmitSearchWithSlowModelWatchAsync();");

        string querySubmitted = ExtractMethodWindow(MainWindowSource, "OnQuerySubmitted", 1700);
        AssertContainsInOrder(querySubmitted,
            "CollapseAdvancedOptionsForSearch();",
            "await SubmitSearchWithSlowModelWatchAsync();");

        string autoSearch = ExtractMethodWindow(MainWindowSource, "OnContentLoaded", 2400);
        AssertContainsInOrder(autoSearch,
            "if (await RunPreSearchWarningGatesAsync())",
            "CollapseAdvancedOptionsForSearch();",
            "await ViewModel.StartSearchAsync();");

        string helper = ExtractMethodWindow(MainWindowSource, "CollapseAdvancedOptionsForSearch", 500);
        Assert.Contains("AdvancedOptionsFlyout?.Hide();", helper);
    }

    [Fact]
    public void SearchStart_MonitorsTempDriveAndReportsLowDiskTermination()
    {
        AssertContainsInOrder(MainViewModelSource,
            "Task? lowDiskMonitorTask = null;",
            "lowDiskMonitorTask = StartLowDiskSpaceMonitor(runId, cts, _resultStore);",
            "if (_lowDiskSpaceCancellation is { } lowDiskSpace)",
            "var message = LowDiskSpaceMonitor.BuildTerminationMessage(lowDiskSpace);",
            "ErrorText = message;",
            "await lowDiskMonitorTask.ConfigureAwait(true);");

        string monitor = ExtractMethodWindow(MainViewModelSource, "StartLowDiskSpaceMonitor", 1500);
        AssertContainsInOrder(monitor,
            "var tempFilePath = resultStore?.TempFilePath;",
            "var fullThreshold = LowDiskSpaceMonitor.PercentToThreshold(LowDiskSpaceWarningPercent);",
            "return LowDiskSpaceMonitor.StartAsync(",
            "fullThreshold,",
            "LowDiskSpaceMonitor.DefaultCheckInterval,",
            "lowDiskSpace =>",
            "if (!IsCurrentSearch(runId, cts))",
            "_lowDiskSpaceCancellation = lowDiskSpace;",
            "cts.Cancel();");

        Assert.Contains("LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(_settings.LowDiskSpaceWarningPercent);", MainViewModelSource);
        Assert.Contains("_settings.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(LowDiskSpaceWarningPercent);", MainViewModelSource);
        Assert.Contains("Temp-drive full warning threshold (%):", SettingsWindowSource);
        Assert.Contains("var lowDiskWarning = new NumberBox { Value = _viewModel.LowDiskSpaceWarningPercent, Minimum = AppSettings.MinimumLowDiskSpaceWarningPercent, Maximum = AppSettings.MaximumLowDiskSpaceWarningPercent };", SettingsWindowSource);
        Assert.Contains("_viewModel.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent((int)args.NewValue);", SettingsWindowSource);
    }

    [Fact]
    public void AdvancedOptionsDrawer_UsesLightDismissFlyoutWithoutGrowingWindow()
    {
        // The drawer is a flat toggle button whose Flyout drops over the desktop
        // (ShouldConstrainToRootBounds=False), so the window never grows vertically in either
        // launcher or traditional mode.
        string toggle = ExtractXamlWindow("x:Name=\"AdvancedOptionsToggle\"", 3500);
        Assert.Contains("Grid.Column=\"0\"", toggle);
        Assert.Contains("<TextBlock Text=\"Advanced Options\"", toggle);
        Assert.Contains("x:Name=\"AdvancedOptionsExpandGlyph\"", toggle);
        Assert.Contains("<Button.Flyout>", toggle);
        Assert.Contains("x:Name=\"AdvancedOptionsFlyout\"", toggle);
        Assert.Contains("ShouldConstrainToRootBounds=\"False\"", toggle);
        Assert.Contains("Placement=\"BottomEdgeAlignedLeft\"", toggle);
        Assert.Contains("Opened=\"OnAdvancedOptionsFlyoutOpened\"", toggle);
        Assert.Contains("Closed=\"OnAdvancedOptionsFlyoutClosed\"", toggle);
        Assert.Contains("x:Name=\"AdvancedOptionsScrollViewer\"", toggle);

        // The Expander and the in-window overlay host/scrim are gone.
        Assert.DoesNotContain("x:Name=\"AdvancedOptionsExpander\"", MainWindowXaml);
        Assert.DoesNotContain("x:Name=\"AdvancedOptionsOverlayHost\"", MainWindowXaml);
        Assert.DoesNotContain("x:Name=\"AdvancedOptionsOverlayScrim\"", MainWindowXaml);

        // Flyout open/close drives the chevron glyph + sizing.
        string opened = ExtractMethodWindow(MainWindowSource, "OnAdvancedOptionsFlyoutOpened", 500);
        AssertContainsInOrder(opened,
            "AdvancedOptionsExpandGlyph.Glyph = \"\\uE70E\";",
            "SyncAdvancedOptionsDrawerWidth();",
            "UpdateAdvancedOptionsDrawerMaxHeight();");
        string closed = ExtractMethodWindow(MainWindowSource, "OnAdvancedOptionsFlyoutClosed", 300);
        Assert.Contains("AdvancedOptionsExpandGlyph.Glyph = \"\\uE70D\";", closed);

        string width = ExtractMethodWindow(MainWindowSource, "SyncAdvancedOptionsDrawerWidth", 700);
        Assert.Contains("SearchCardBottomBar.ActualWidth", width);
        Assert.Contains("AdvancedOptionsScrollViewer.Width = width;", width);

        // Max-height is bounded by the monitor work area (the flyout is its own visual root).
        string maxHeight = ExtractMethodWindow(MainWindowSource, "UpdateAdvancedOptionsDrawerMaxHeight", 1600);
        Assert.Contains("if (!IsAdvancedOptionsDrawerOpen)", maxHeight);
        Assert.Contains("DisplayArea.GetFromWindowId(", maxHeight);
        Assert.Contains("AdvancedOptionsScrollViewer.MaxHeight =", maxHeight);

        // The window-growth + overlay code paths are removed entirely.
        Assert.DoesNotContain("FitTraditionalWindowHeightToContent", MainWindowSource);
        Assert.DoesNotContain("SetAdvancedOptionsDrawerExpandedWidthState", MainWindowSource);
        Assert.DoesNotContain("MoveAdvancedOptionsDrawerToOverlay", MainWindowSource);
        Assert.DoesNotContain("InitializeTraditionalAdvancedOptionsOverlay", MainWindowSource);

        Assert.DoesNotContain("Advanced Options drawer width:", SettingsWindowSource);
        Assert.DoesNotContain("Fill search box width when collapsed and expanded", SettingsWindowSource);
        Assert.DoesNotContain("Compact when collapsed", SettingsWindowSource);
    }

    [Fact]
    public void TerminalChevron_MovesIntoExpandedTerminalAndFreesSearchPanelSpace()
    {
        string bottomActions = ExtractXamlWindow("x:Name=\"SearchCardBottomActions\"", 1400);
        Assert.Contains("Grid.Column=\"1\"", bottomActions);
        Assert.Contains("Orientation=\"Horizontal\" Spacing=\"6\"", bottomActions);
        Assert.Contains("VerticalAlignment=\"Center\"", bottomActions);
        Assert.DoesNotContain("VerticalAlignment=\"Bottom\"", bottomActions);
        AssertContainsInOrder(bottomActions,
            "x:Name=\"SearchCardLoadSessionButton\"",
            "Click=\"OnLoadSession\"",
            "Style=\"{StaticResource SearchCardBottomIconButtonStyle}\"",
            "x:Name=\"PreSearchTerminalChevron\"",
            "Click=\"OnToggleTerminalPane\"",
            "Style=\"{StaticResource SearchCardBottomIconButtonStyle}\"");
        Assert.Contains("x:Key=\"SearchCardBottomIconButtonStyle\"", MainWindowXaml);
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", MainWindowXaml);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"{ThemeResource CardStrokeColorDefaultBrush}\" />", MainWindowXaml);

        string preSearchChevron = ExtractXamlWindow("x:Name=\"PreSearchTerminalChevron\"", 900);
        Assert.Contains("Click=\"OnToggleTerminalPane\"", preSearchChevron);
        Assert.DoesNotContain("Grid.RowSpan", preSearchChevron);
        Assert.DoesNotContain("Canvas.ZIndex", preSearchChevron);
        Assert.Contains("x:Name=\"PreSearchTerminalChevronIcon\"", preSearchChevron);
        Assert.Contains("ToolTipService.ToolTip=\"Toggle embedded terminal\"", preSearchChevron);

        string terminalHost = ExtractXamlWindow("x:Name=\"TerminalHost\"", 5600);
        AssertContainsInOrder(terminalHost,
            "<ComboBox x:Name=\"TerminalShellSelector\"",
            "SelectionChanged=\"OnTerminalShellSelectionChanged\"",
            "<WebView2 x:Name=\"TerminalWebView\"",
            "<Button x:Name=\"TerminalChevron\"",
            "HorizontalAlignment=\"Right\"",
            "VerticalAlignment=\"Bottom\"",
            "Margin=\"0,0,32,10\"",
            "Canvas.ZIndex=\"10\"",
            "<FontIcon x:Name=\"TerminalChevronIcon\"");

        Assert.Contains("<Grid RowSpacing=\"12\" ColumnSpacing=\"8\">", MainWindowXaml);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", MainWindowXaml);
        Assert.Contains("<Grid Grid.Row=\"1\" Grid.Column=\"1\" Grid.ColumnSpan=\"2\">", MainWindowXaml);
        Assert.Contains("x:Name=\"QueryBox\"", MainWindowXaml);
        Assert.Contains("Grid.Column=\"0\"", ExtractXamlWindow("x:Name=\"AdvancedOptionsToggle\"", 400));

        string terminalToggle = ExtractMethodWindow(MainWindowSource, "SetTerminalPaneExpanded", 1200);
        AssertContainsInOrder(terminalToggle,
            "UpdateTerminalChevronGlyphs();",
            "UpdateTerminalChevronVisibility();",
            "if (_launcherMode)",
            "PositionLauncherWindow();");

        string glyphSync = ExtractMethodWindow(MainWindowSource, "UpdateTerminalChevronGlyphs", 800);
        AssertContainsInOrder(glyphSync,
            "string glyph = _terminalPaneExpanded ? \"\\uE70E\" : \"\\uE70D\";",
            "TerminalChevronIcon.Glyph = glyph;",
            "PreSearchTerminalChevronIcon.Glyph = glyph;");

        string visibilitySync = ExtractMethodWindow(MainWindowSource, "UpdateTerminalChevronVisibility", 1000);
        AssertContainsInOrder(visibilitySync,
            "SearchCardBottomActions.Visibility = Visibility.Visible;",
            "PreSearchTerminalChevron.Visibility = Visibility.Visible;",
            "TerminalChevron.Visibility = _terminalPaneExpanded ? Visibility.Visible : Visibility.Collapsed;");
        Assert.DoesNotContain("var showSearchCardBottomActions = !_terminalPaneExpanded;", visibilitySync);
        Assert.DoesNotContain("&& !_advancedOptionsDrawerExpandedWidth", visibilitySync);

        string statusBarVisibility = ExtractMethodWindow(MainWindowSource, "UpdateBottomStatusBarVisibility", 900);
        Assert.Contains("UpdateTerminalChevronVisibility();", statusBarVisibility);
    }

    [Fact]
    public void SearchCard_LoadSessionButtonSitsBesideTerminalChevron()
    {
        string searchBar = ExtractXamlWindow("<StackPanel Grid.Column=\"2\" Orientation=\"Horizontal\" Spacing=\"6\" VerticalAlignment=\"Center\">", 4800);
        Assert.Contains("x:Name=\"SearchSplitButton\"", searchBar);
        Assert.Contains("x:Name=\"SearchCancelButton\"", searchBar);
        Assert.DoesNotContain("x:Name=\"SearchCardLoadSessionButton\"", searchBar);

        string bottomActions = ExtractXamlWindow("x:Name=\"SearchCardBottomActions\"", 1400);
        AssertContainsInOrder(bottomActions,
            "x:Name=\"SearchCardLoadSessionButton\"",
            "Click=\"OnLoadSession\"",
            "ToolTipService.ToolTip=\"Load a previously saved .yagu-session file\"",
            "<FontIcon Glyph=\"&#xE8E5;\"",
            "x:Name=\"PreSearchTerminalChevron\"",
            "Click=\"OnToggleTerminalPane\"");

        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.IsSessionIdle, Mode=OneWay}\"", bottomActions);

        string compactState = ExtractMethodWindow(MainWindowSource, "ApplyTopSearchDrawerCompactState", 1400);
        AssertContainsInOrder(compactState,
            "SearchCardLoadSessionButton.Width = CompactTopSearchActionButtonWidth;",
            "SearchCardLoadSessionButton.Height = CompactTopSearchActionButtonWidth;",
            "SearchCardLoadSessionButton.Width = 32;",
            "SearchCardLoadSessionButton.Height = 32;");
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
        Assert.Contains("HorizontalScrollBarVisibility=\"Visible\"", previewScrollViewer);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", previewScrollViewer);

        string horizontalScrollHelper = ExtractMethodWindow(MainWindowSource, "SetHorizontalPreviewScroll", 700);
        AssertContainsInOrder(horizontalScrollHelper,
            "scrollViewer.HorizontalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;",
            "scrollViewer.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;");

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", 6500);
        AssertContainsInOrder(addSection,
            "var sectionScroller = new ScrollViewer",
            "HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled",
            "HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden");

        string hook = ExtractMethodWindow(MainWindowSource, "EnsurePreviewViewChangedHooked", 1800);
        AssertContainsInOrder(hook,
            "PreviewScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent,",
            "new PointerEventHandler(OnPreviewPointerWheelChanged)",
            "handledEventsToo: true);");

        string wheel = ExtractMethodWindow(MainWindowSource, "OnPreviewPointerWheelChanged", 2600);
        AssertContainsInOrder(wheel,
            "IsPreviewZoomModifierActive(e)",
            "AdjustPreviewTextZoom(zoomProps.MouseWheelDelta);",
            "e.Handled = true;",
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
        string ctor = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 5000);
        Assert.Contains("AttachPreviewSelectionAutoScroll(PreviewBlock);", ctor);
        Assert.Contains("ConfigurePreviewSelectionMode(PreviewBlock);", ctor);

        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 8000);
        Assert.Contains("AttachPreviewSelectionAutoScroll(block);", addSection);
        Assert.Contains("IsTextSelectionEnabled = false,", addSection);

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

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyPreviewSelectionAutoScroll", window: 8200);
        AssertContainsInOrder(apply,
            "var verticalScroller = _previewSelectionAutoScrollVerticalScroller ?? scroller;",
            "bool canScrollHorizontally = block.TextWrapping == TextWrapping.NoWrap",
            "scroller.HorizontalScrollMode == ScrollMode.Enabled",
            "bool canScrollVertically = verticalScroller.VerticalScrollMode != ScrollMode.Disabled",
            "TryGetPreviewSelectionAutoScrollVelocity(scroller, _previewSelectionAutoScrollPointerX, out velocity)",
            "TryGetPreviewSelectionAutoScrollVerticalVelocity(verticalScroller, _previewSelectionAutoScrollPointerYInVertical, out verticalVelocity)",
            "double step = velocity * elapsedSeconds;",
            "double targetX = Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth);",
            "double targetY = Math.Clamp(verticalScroller.VerticalOffset + verticalStep, 0, verticalScroller.ScrollableHeight);",
            "if (ReferenceEquals(scroller, verticalScroller))",
            "verticalScroller.ChangeView(null, targetY, null, disableAnimation: true);",
            "UpdatePreviewCustomSelectionFromCurrentPointer();");

        // Regression guard (preview-selection freeze, 2026-06-30): the stuck-scroller stop must fire
        // whenever the offset stops moving across frames — INCLUDING when ChangeView is repeatedly
        // REJECTED (accepted == false). The old guard required "&& accepted", so a ScrollViewer that
        // kept rejecting ChangeView during a wide single-line drag-select spun the 16 ms auto-scroll
        // timer forever, calling the expensive UpdatePreviewCustomSelectionFromCurrentPointer() on
        // every no-progress frame until the app hung at ~25 GB. The stop must NOT depend on `accepted`.
        Assert.Contains("StopPreviewSelectionAutoScrollTimer(\"stuck-scroller\")", apply);
        Assert.DoesNotContain("&& accepted", apply);

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

        string press = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollPointerPressed", window: 3800);
        AssertContainsInOrder(press,
            "if (!ShouldUseCustomPreviewSelection(block, scroller))",
            "ClearPreviewCustomSelection();",
            "return;",
            "bool isDoubleClick =",
            "_ = EnterPreviewEditorFromPointerDoubleClickAsync(block, pressPoint);",
            "_previewSelectionLastClickBlock = block;",
            "bool pointerCaptured = block.CapturePointer(e.Pointer);",
            "BeginPreviewCustomSelection(block, scroller);",
            "e.Handled = true;");

        string selectionMode = ExtractMethodWindow(MainWindowSource, "ConfigurePreviewSelectionMode", window: 1200);
        AssertContainsInOrder(selectionMode,
            "if (block.IsTextSelectionEnabled)",
            "block.IsTextSelectionEnabled = false;");
        Assert.DoesNotContain("useNativeSelection", selectionMode);

        string highlighter = ExtractMethodWindow(MainWindowSource, "UpdatePreviewCustomSelectionHighlighter", window: 2200);
        AssertContainsInOrder(highlighter,
            "ReferenceEquals(_previewCustomSelectionLastRangeBlock, block)",
            "DrawPreviewCustomSelectionOverlay(block, startIndex, endIndex)",
            "_previewCustomSelectionLastRangeEnd = endIndex;");
        Assert.DoesNotContain("block.TextHighlighters.Add(_previewCustomSelectionHighlighter);", highlighter);

        string overlay = ExtractMethodWindow(MainWindowSource, "DrawPreviewCustomSelectionOverlay", window: 8600);
        AssertContainsInOrder(overlay,
            "PreviewSelectionOverlay.ActualWidth > 0",
            "PreviewScrollViewer.ActualWidth",
            // The highlight bands are clamped to the visible content region: the right edge
            // stops at the viewport (never over the scrollbar), the left edge stops at the
            // content column (never over the inline line-number gutter).
            "double contentRightBound = overlayWidth;",
            "PreviewScrollViewer.ViewportWidth",
            "bool hasInlineGutter = !_sectionGutterBlocks.ContainsKey(block);",
            "foreach (var textBlock in block.Blocks)",
            "int paragraphStart = blockIndex;",
            "int paragraphEnd = paragraphStart + paragraphLength;",
            "int rangeStart = Math.Max(selectionStart, paragraphStart);",
            "block.TransformToVisual(PreviewSelectionOverlay)",
            "TryGetParagraphInlineGutterLength(paragraph, out int gutterCharLength)",
            "double visibleLeft = Math.Max(contentLeftBound, left);",
            "double visibleRight = Math.Min(contentRightBound, right);",
            "GetPreviewCustomSelectionOverlayMarker(markerIndex++)",
            "PreviewSelectionOverlay.Visibility = Visibility.Visible;");

        // The inline gutter length is recorded so the overlay can find the content column.
        string makeParagraph = ExtractMethodWindow(MainWindowSource, "MakePreviewParagraph", window: 3200);
        Assert.Contains("s_paragraphInlineGutterLength.AddOrUpdate(para,", makeParagraph);
        Assert.Contains("<Canvas x:Name=\"PreviewSelectionOverlay\"", MainWindowXaml);
        Assert.DoesNotContain("x:Name=\"PreviewSelectionOverlay\" Grid.Row=\"1\"\r\n                            HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"\r\n                            Canvas.ZIndex=\"19\"\r\n                            IsHitTestVisible=\"False\" Visibility=\"Collapsed\"", MainWindowXaml);

        string pointerMapping = ExtractMethodWindow(MainWindowSource, "MapPreviewTextPointerToBlockIndex", window: 2600);
        Assert.Contains("MapPreviewTextPointerToParagraphIndex(paragraph, pointerOffset, paragraphLength)", pointerMapping);
        string paragraphMapping = ExtractMethodWindow(MainWindowSource, "MapPreviewTextPointerToParagraphIndex", window: 2600);
        AssertContainsInOrder(paragraphMapping,
            "foreach (var inline in paragraph.Inlines)",
            "inline is not Run run",
            "int runStart = run.ContentStart.Offset;",
            "int runEnd = run.ContentEnd.Offset;",
            "return Math.Clamp(localIndex + pointerOffset - runStart, 0, paragraphLength);");

        string copy = ExtractMethodWindow(MainWindowSource, "CopyPreviewSelection", window: 1200);
        Assert.Contains("TryBuildPreviewCustomSelectionText(block, withLineNumbers, out string customSelectedText)", copy);
    }

    [Fact]
    public void PreviewSelectionDrag_AutoScrollsVerticallyViaOuterScroller()
    {
        // Sections drawers have their inner scroller's vertical scrolling disabled
        // (VerticalScrollBarVisibility = Disabled), so the shared outer
        // PreviewScrollViewer is the real vertical scroller. Vertical drag-select
        // auto-scroll must drive that outer scroller, not the inner section scroller.
        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 8000);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,", addSection);

        string verticalResolver = ExtractMethodWindow(
            MainWindowSource, "ResolvePreviewSelectionAutoScrollVerticalScroller", window: 600);
        Assert.Contains("=> PreviewScrollViewer;", verticalResolver);

        // Pressed handler captures the pointer position relative to the vertical scroller.
        string press = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollPointerPressed", window: 3800);
        AssertContainsInOrder(press,
            "_previewSelectionAutoScrollVerticalScroller = ResolvePreviewSelectionAutoScrollVerticalScroller(block);",
            "_previewSelectionAutoScrollPointerPointInVerticalScroller =",
            "_previewSelectionAutoScrollPointerYInVertical = _previewSelectionAutoScrollPointerPointInVerticalScroller.Y;");

        // Move handler keeps the vertical-scroller pointer position and edge check current.
        string moved = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollPointerMoved", window: 3000);
        AssertContainsInOrder(moved,
            "var verticalScroller = _previewSelectionAutoScrollVerticalScroller;",
            "e.GetCurrentPoint(verticalScroller).Position;",
            "_previewSelectionAutoScrollPointerYInVertical =",
            "TryGetPreviewSelectionAutoScrollVerticalVelocity(",
            "verticalScroller,",
            "_previewSelectionAutoScrollPointerYInVertical,");

        // The separate-scroller branch issues the vertical ChangeView against the outer scroller.
        string apply = ExtractMethodWindow(MainWindowSource, "ApplyPreviewSelectionAutoScroll", window: 8200);
        AssertContainsInOrder(apply,
            "if (horizontalMoved)",
            "scroller.ChangeView(targetX, null, null, disableAnimation: true);",
            "if (verticalMoved)",
            "verticalScroller.ChangeView(null, targetY, null, disableAnimation: true);");

        // Selection index follows the outer scroll offset under a held-still pointer.
        string resolveIndex = ExtractMethodWindow(
            MainWindowSource, "TryResolvePreviewSelectionIndexFromCurrentPointer", window: 2600);
        AssertContainsInOrder(resolveIndex,
            "var verticalScroller = _previewSelectionAutoScrollVerticalScroller;",
            "!ReferenceEquals(verticalScroller, scroller)",
            "verticalScroller.TransformToVisual(block)",
            "_previewSelectionAutoScrollPointerPointInVerticalScroller",
            "blockPoint.Y = verticalBlockPoint.Y;");
    }

    [Fact]
    public void PreviewSelection_DisablesNativeSelection_AndUsesCustomOverlayForWrap()
    {
        // The native double-tap word-select hit-test (TextSelectionManager::OnDoubleTapped
        // -> RichTextBlockView::GetCharacterIndex) faults against a mid-reflow
        // RichTextBlock, so native selection is permanently disabled and the custom
        // overlay selection drives every mode.
        string selectionMode = ExtractMethodWindow(MainWindowSource, "ConfigurePreviewSelectionMode", window: 1200);
        Assert.Contains("block.IsTextSelectionEnabled = false;", selectionMode);
        Assert.DoesNotContain("useNativeSelection", selectionMode);

        // Content blocks are created with native selection already off.
        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 8000);
        Assert.Contains("IsTextSelectionEnabled = false,", addSection);
        Assert.DoesNotContain("IsTextSelectionEnabled = wrap,", addSection);

        // Custom overlay selection applies in both wrap and no-wrap modes.
        string shouldUse = ExtractMethodWindow(MainWindowSource, "ShouldUseCustomPreviewSelection", window: 700);
        Assert.Contains("=> true;", shouldUse);

        // The overlay renders wrapped selections row-by-row instead of one flat strip.
        string overlay = ExtractMethodWindow(MainWindowSource, "DrawPreviewCustomSelectionOverlay", window: 6800);
        AssertContainsInOrder(overlay,
            "block.TextWrapping == TextWrapping.Wrap",
            "TryBuildWrappedPreviewSelectionRows(",
            "GetPreviewCustomSelectionOverlayMarker(markerIndex++)");

        // The wrapped-row builder resolves real character rects and emits up to three bands.
        string wrapped = ExtractMethodWindow(MainWindowSource, "TryBuildWrappedPreviewSelectionRows", window: 4000);
        AssertContainsInOrder(wrapped,
            "GetPreviewParagraphTextPointerAtIndex(paragraph, localStart)",
            "startPointer.GetCharacterRect(LogicalDirection.Forward)",
            "endPointer.GetCharacterRect(LogicalDirection.Backward)",
            "bool sameRow = Math.Abs(startRect.Y - endRect.Y) <= rowHeight * 0.5;",
            "AddOverlayBandRect(toOverlay, startRect.X, startRect.Y, contentRightBlock, startRect.Y + rowHeight,");

        // The failed suspend/resume machinery is fully removed.
        Assert.DoesNotContain("SuspendPreviewNativeSelectionDuringLayoutChurn", MainWindowSource);
        Assert.DoesNotContain("_previewNativeSelectionSuspended", MainWindowSource);
    }

    [Fact]
    public void PreviewDoubleClick_DetectedInPointerHandler_OpensEditor()
    {
        // Native text selection is disabled to avoid the word-select crash, which
        // also suppresses the RichTextBlock DoubleTapped gesture (the custom
        // selection captures the pointer and marks the press handled). The pointer
        // handler must therefore detect the double-click itself and open the inline
        // editor so double-clicking any preview text still jumps to that line.
        string press = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectionAutoScrollPointerPressed", window: 3800);
        AssertContainsInOrder(press,
            "Point pressPoint = e.GetCurrentPoint(block).Position;",
            "bool isDoubleClick =",
            "ReferenceEquals(_previewSelectionLastClickBlock, block)",
            "pressTick - _previewSelectionLastClickTick <= PreviewSelectionDoubleClickMaxMs",
            "Math.Abs(pressPoint.X - _previewSelectionLastClickPoint.X) <= PreviewSelectionDoubleClickMaxDistance",
            "StopPreviewSelectionAutoScroll(\"double-click-editor\");",
            "ClearPreviewCustomSelection();",
            "e.Handled = true;",
            "_ = EnterPreviewEditorFromPointerDoubleClickAsync(block, pressPoint);");

        // The helper mirrors the native DoubleTapped path and records the open tick.
        string helper = ExtractMethodWindow(MainWindowSource, "EnterPreviewEditorFromPointerDoubleClickAsync", window: 1200);
        AssertContainsInOrder(helper,
            "if (_previewMutating)",
            "DismissActiveIntroTip();",
            "_previewEditorPointerOpenTick = Environment.TickCount64;",
            "var filePath = ResolvePreviewBlockFilePath(block);",
            "await TryEnterPreviewEditorAtPointAsync(block, point, filePath);");

        // The native DoubleTapped handler skips when the pointer path already opened
        // the editor for this double-click, so it never opens twice.
        string onDouble = ExtractMethodWindow(MainWindowSource, "OnPreviewBlockDoubleTapped", window: 900);
        Assert.Contains("Environment.TickCount64 - _previewEditorPointerOpenTick < PreviewEditorPointerOpenGuardMs", onDouble);

        // Editor entry uses a side-effect-free layout check that resolves on the FIRST
        // call — not the stateful overlay-centering settle ladder (which returns false
        // on first contact and silently swallowed the double-click after a scroll).
        string laidOut = ExtractMethodWindow(MainWindowSource, "IsPreviewSectionBodyLaidOutForPointer", window: 1800);
        Assert.Contains("if (!expander.IsExpanded)", laidOut);
        Assert.DoesNotContain("_activeOverlayStablePasses", laidOut);
        Assert.DoesNotContain("requiredStablePasses", laidOut);
    }

    [Fact]
    public void ResultsListScroll_PreservesPinnedTopDuringLiveUpdates()
    {
        string ctor = ExtractMethodWindow(MainWindowSource, "MainWindow", window: 5000);
        Assert.Contains("InitializeResultsListSmartScroll();", ctor);

        string initialize = ExtractMethodWindow(MainWindowSource, "InitializeResultsListSmartScroll", window: 2200);
        AssertContainsInOrder(initialize,
            "ViewModel.ResultRows.CollectionChanging += OnResultGroupsChanging;",
            "ViewModel.ResultRows.CollectionChanged += OnResultGroupsCollectionChanged;",
            "ResultsList.Loaded +=",
            "EnsureResultsListScrollViewerHooked();",
            "CaptureResultsListScrollPosition();");

        string changing = ExtractMethodWindow(MainWindowSource, "OnResultGroupsChanging", window: 1200);
        AssertContainsInOrder(changing,
            "CaptureResultsListScrollPosition();",
            "ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();",
            "!ViewModel.IsSearching",
            "ResultRowsChanging: intent={intent}",
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
            "bool hasGroupsWithoutScroller = ViewModel.ResultRows.Count > 0;",
            "_resultsListWasAtTop = hasGroupsWithoutScroller;",
            "_resultsListWasAtBottom = hasGroupsWithoutScroller;",
            "bool hasVisibleGroups = ViewModel.ResultRows.Count > 0;",
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
            "ResultsList.ScrollIntoView(ViewModel.ResultRows[0], ScrollIntoViewAlignment.Leading);",
            "_resultsListScrollViewer?.ChangeView(null, 0, null, disableAnimation: true);",
            "ScrollResultsListToTop: rows={ViewModel.ResultRows.Count}, groups={ViewModel.ResultGroups.Count}");

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

        string collapsed = ExtractMethodWindow(MainWindowSource, "ClearVisibleResultsAfterCollapseAsync", window: 2200);
        AssertContainsInOrder(collapsed,
            "DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low",
            "if (!group.IsExpanded)",
            "group.ClearVisibleResults();");

        string expanding = ExtractMethodWindow(MainWindowSource, "OnFileGroupExpanding", window: 2200);
        AssertContainsInOrder(expanding,
            "if (!g.HasContentMatches)",
            "g.IsExpanded = false;",
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
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("DefaultSelectedPreviewContentBackgroundColor = \"#FF000000\"", settingsSource);
        Assert.Contains("DefaultUnselectedPreviewContentBackgroundColor = \"#FF1E1E1E\"", settingsSource);

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
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        string textControlBoxSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "TextControlBox.cs"));

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
    public void DeveloperOptions_LogFilePath_IsClickableLinkThatOpensNotepad()
    {
        // The log file path in Developer Options is a Hyperlink whose Click opens the active log in Notepad.
        Assert.Contains("var logHyperlink = new Microsoft.UI.Xaml.Documents.Hyperlink();", SettingsWindowSource);
        Assert.Contains("logHyperlink.Click += (_, _) => OpenLogFileInNotepad(logPath);", SettingsWindowSource);
        Assert.Contains("ToolTipService.SetToolTip(logFileBlock, \"Open the log file in Notepad\");", SettingsWindowSource);

        string openLogMethod = ExtractMethodWindow(SettingsWindowSource, "OpenLogFileInNotepad", window: 600);
        AssertContainsInOrder(openLogMethod,
            "Path.Combine(System.Environment.SystemDirectory, \"notepad.exe\")",
            "Arguments = $\"\\\"{logPath}\\\"\",",
            "UseShellExecute = false,");
    }

    [Fact]
    public void SelectAll_TargetsSectionContentBlock_NotGutterBlock()
    {
        // Ctrl+A in section preview must select the CONTENT block, not the per-section gutter
        // (line-number) block. The gutter block is first in the visual tree, so the old
        // FindFirstRichTextBlock fallback selected it — yielding nothing for file-name-only previews
        // (where no content match was clicked first to seed _previewCustomSelectionBlock).
        string selectAll = ExtractMethodWindow(MainWindowSource, "TrySelectAllPreviewContent", window: 2000);
        Assert.Contains("FindFirstSectionContentRichTextBlock(fe)", selectAll);
        Assert.DoesNotContain("FindFirstRichTextBlock(fe)", selectAll);

        // The content-block finder must skip gutter blocks (the values of _sectionGutterBlocks) by
        // only returning RichTextBlocks that are registered as content blocks (its keys).
        string finder = ExtractMethodWindow(MainWindowSource, "FindFirstSectionContentRichTextBlock", window: 600);
        Assert.Contains("_sectionGutterBlocks.ContainsKey(rtb) ? rtb : null", finder);
    }

    [Fact]
    public void EditorTextColor_IsConfigurableWithThemeAutoDefault()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        string textControlBoxSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "TextControlBox.cs"));

        // Empty string is the "Auto" sentinel (follow the app/system theme) and must be normalized in the
        // shared migration path so both Load() and LoadAsync() preserve it.
        Assert.Contains("DefaultPreviewEditorTextColor = \"\"", settingsSource);
        Assert.Contains("settings.PreviewEditorTextColor = NormalizeEditorTextColor(settings.PreviewEditorTextColor);", settingsSource);

        Assert.Contains("PreviewEditorTextColor", viewModelSource);
        Assert.Contains("_settings.PreviewEditorTextColor = string.IsNullOrWhiteSpace(PreviewEditorTextColor)", viewModelSource);

        // Editor body-text color must be exposed on the vendored editor and applied alongside the gutter color.
        Assert.Contains("public Windows.UI.Color TextColor", textControlBoxSource);

        string applyColors = ExtractMethodWindow(MainWindowSource, "ApplyPreviewColors", window: 1800);
        AssertContainsInOrder(applyColors,
            "PreviewEditor.LineNumberColor = ColorStringHelper.Parse(vm.PreviewEditorGutterColor",
            "PreviewEditor.TextColor = ResolveEffectiveEditorTextColor();");

        // Settings UI surfaces both the Auto override toggle and the color row.
        Assert.Contains("Override editor text color", SettingsWindowSource);
        Assert.Contains("Editor text:", SettingsWindowSource);
    }

    [Fact]
    public void DeveloperOptions_HidesMemoryPressureWarningLabelByDefault()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowMemoryPressureWarningLabel { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowMemoryPressureWarningLabel { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
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
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowAutoScrollResultsCheckbox { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowAutoScrollResultsCheckbox { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
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
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowStatsForNerds { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowStatsForNerds { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
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
    public void DeveloperOptions_HidesBuildNumberInTitleBarByDefault()
    {
        string settingsSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool ShowBuildNumberInTitleBar { get; set; }", settingsSource);
        Assert.DoesNotContain("ShowBuildNumberInTitleBar { get; set; } = true", settingsSource);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("[ObservableProperty] public partial bool ShowBuildNumberInTitleBar { get; set; }", viewModelSource);
        Assert.DoesNotContain("ShowBuildNumberInTitleBar { get; set; } = true", viewModelSource);
        Assert.Contains("ShowBuildNumberInTitleBar = _settings.ShowBuildNumberInTitleBar;", viewModelSource);
        Assert.Contains("_settings.ShowBuildNumberInTitleBar = ShowBuildNumberInTitleBar;", viewModelSource);

        AssertContainsInOrder(MainWindowSource,
            "private static string AppTitleWithoutBuildNumber => $\"{AppInfo.Name} - {AppInfo.Description}\";",
            "private static string BuildAppWindowTitle(bool showBuildNumberInTitleBar)",
            "showBuildNumberInTitleBar ? $\"{AppTitleWithoutBuildNumber} {AppInfo.Version}\" : AppTitleWithoutBuildNumber;",
            "private string CurrentAppWindowTitle => BuildAppWindowTitle(ViewModel.ShowBuildNumberInTitleBar);",
            "private void ApplyAppWindowTitle()",
            "Title = title;",
            "AppTitleText.Text = title;");
        Assert.Contains("if (e.PropertyName == nameof(ViewModel.ShowBuildNumberInTitleBar))", MainWindowSource);
        Assert.Contains("ApplyAppWindowTitle();", MainWindowSource);
        Assert.DoesNotContain("Title = AppInfo.WindowTitle;", MainWindowSource);
        Assert.DoesNotContain("AppTitleText.Text = AppInfo.WindowTitle;", MainWindowSource);
        Assert.Contains("new HelpWindow(_hwnd, helpPath, CurrentAppWindowTitle);", MainWindowSource);
        Assert.Contains("public HelpWindow(IntPtr mainHwnd, string helpPath, string appTitle)", HelpWindowSource);
        Assert.Contains("AppTitleText.Text = appTitle;", HelpWindowSource);

        Assert.Contains("Show build number in title bar", SettingsWindowSource);
        Assert.Contains("IsChecked = _viewModel.ShowBuildNumberInTitleBar", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowBuildNumberInTitleBar = true", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowBuildNumberInTitleBar = false", SettingsWindowSource);
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
        Assert.Contains("<Flyout x:Name=\"ResultsOptionsFlyout\" Placement=\"BottomEdgeAlignedRight\" Opened=\"OnResultsOptionsFlyoutOpened\">", toolbar);

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
        Assert.Contains("<FontIcon Glyph=\"&#xE8E6;\" FontSize=\"14\" />", toolbar);
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
        Assert.Contains("x:Name=\"SortNoneButton\"", sortFlyout);
        Assert.Contains("Background=\"Transparent\"", sortFlyout);
        Assert.Contains("BorderThickness=\"0\"", sortFlyout);

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
            "ApplySortNoneState();",
            "UpdateSortDirectionButtons(SortMatchesAscButton, SortMatchesDescButton, 1);",
            "UpdateSortDirectionButtons(SortDateModifiedAscButton, SortDateModifiedDescButton, 2);",
            "UpdateSortDirectionButtons(SortFileSizeAscButton, SortFileSizeDescButton, 3);",
            "UpdateSortDirectionButtons(SortFileNameAscButton, SortFileNameDescButton, 4);");

        string noneState = ExtractMethodWindow(MainWindowSource, "ApplySortNoneState", 1000);
        AssertContainsInOrder(noneState,
            "ViewModel.SortCriteria.Count == 0",
            "SortNoneButton.Foreground = foreground;",
            "SortNoneButton.Background = background;",
            "SortNoneButton.Opacity = selected ? 1.0 : 0.72;");
        Assert.DoesNotContain("SortModeIndex == 0", noneState);

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
            "<MenuFlyout Placement=\"BottomEdgeAlignedLeft\">",
            "<MenuFlyoutSubItem Text=\"By date\">",
            "<MenuFlyoutItem Text=\"Any date\" Click=\"OnDateFilterNone\" />",
            "<MenuFlyoutItem Text=\"Last 5 years\" Click=\"OnDateFilterPastFiveYears\" />",
            "<MenuFlyoutItem x:Name=\"ExtensionFilterMenuItem\" Text=\"By extension\u2026\" Click=\"OnByExtensionFilterMenuClicked\" />");
        Assert.DoesNotContain("OnFilterByExtension", toolbar);

        string filterRow = ExtractXamlWindow("PlaceholderText=\"Filter files…\"", 1700);
        Assert.DoesNotContain("Content=\"{x:Bind ViewModel.DateRangeFilterLabel", filterRow);

        string selectAllFiles = ExtractXamlWindow("x:Name=\"SelectAllFilesCheckBox\"", 700);
        Assert.Contains("Width=\"24\" Height=\"24\" MinWidth=\"0\" MinHeight=\"0\" Padding=\"0\"", selectAllFiles);
        Assert.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", selectAllFiles);
        AssertContainsInOrder(MainWindowXaml,
            "<Grid Width=\"38\" VerticalAlignment=\"Center\">",
            "x:Name=\"SelectAllFilesCheckBox\"",
            "<Grid Grid.Column=\"1\">");

        string extensionMenu = ExtractMethodWindow(MainWindowSource, "PopulateExtensionFilterList", window: 3200);
        AssertContainsInOrder(extensionMenu,
            "_extensionFilterListPanel.Children.Clear();",
            "var options = ViewModel.GetExtensionFilterOptions();",
            "Text = \"No extensions available\",",
            "IsChecked = option.IsSelected,",
            "checkBox.Checked += OnExtensionFilterCheckChanged;",
            "_extensionFilterListPanel.Children.Add(checkBox);");
        Assert.DoesNotContain("ContentDialog", extensionMenu);

        string extensionToggle = ExtractMethodWindow(MainWindowSource, "OnExtensionFilterCheckChanged", window: 2200);
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
    public void SelectAllFilesCheckBox_GuardsAgainstPhantomIndeterminateDash()
    {
        // The select-all checkbox is two-state, but WinUI can still drop it into the indeterminate
        // (null) state and paint a stray "dash" glyph. The XAML wires Loaded + Indeterminate guards
        // that snap it back to a definite state; the null→false correction must NOT run the Unchecked
        // side effect (which would deselect everything).
        string selectAllFiles = ExtractXamlWindow("x:Name=\"SelectAllFilesCheckBox\"", 700);
        Assert.Contains("Loaded=\"OnSelectAllFilesLoaded\"", selectAllFiles);
        Assert.Contains("Indeterminate=\"OnSelectAllFilesIndeterminate\"", selectAllFiles);

        string indeterminate = ExtractMethodWindow(MainWindowSource, "OnSelectAllFilesIndeterminate", 600);
        AssertContainsInOrder(indeterminate,
            "cb.IsChecked is not null",
            "_correctingSelectAllIndeterminate = true;",
            "cb.IsChecked = false;");

        string loaded = ExtractMethodWindow(MainWindowSource, "OnSelectAllFilesLoaded", 700);
        Assert.Contains("VisualStateManager.GoToState(cb,", loaded);

        // The Unchecked side effect must early-return during a phantom-indeterminate correction.
        string unchecked2 = ExtractMethodWindow(MainWindowSource, "OnSelectAllFilesUnchecked", 600);
        AssertContainsInOrder(unchecked2,
            "if (_correctingSelectAllIndeterminate)",
            "return;",
            "g.DeselectAll();");
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

        string resultsExpandButton = ExtractXamlWindow("x:Name=\"ExpandResultsButton\"", 700);
        Assert.Contains("Width=\"28\" Height=\"28\" MinWidth=\"0\" MinHeight=\"0\"", resultsExpandButton);
        Assert.Contains("Padding=\"0\" Margin=\"0,0,8,0\"", resultsExpandButton);
        Assert.DoesNotContain("Padding=\"6,4\"", resultsExpandButton);

        Assert.Contains("<Grid Grid.Row=\"1\" Margin=\"22,6,34,6\" ColumnSpacing=\"6\">", MainWindowXaml);
        Assert.DoesNotContain("<Grid Grid.Row=\"1\" Margin=\"8,6\" ColumnSpacing=\"6\">", MainWindowXaml);

        string viewModelSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        AssertContainsInOrder(viewModelSource,
            "public BatchObservableCollection<object> ResultRows { get; } = new();",
            "public void ToggleResultGroupExpansion(ResultGroupHeaderRow header)",
            "private void RebuildResultRows()",
            "ResultRows.ReplaceAll(rows);");

        string scrollSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.ResultsListScroll.cs"));
        Assert.Contains("ViewModel.ResultRows.CollectionChanging += OnResultGroupsChanging;", scrollSource);
        Assert.Contains("ViewModel.ResultRows.CollectionChanged += OnResultGroupsCollectionChanged;", scrollSource);
        Assert.DoesNotContain("ViewModel.ResultGroups.CollectionChanged += OnResultGroupsCollectionChanged;", scrollSource);

        string fileGroupTemplate = ExtractXamlWindow("<DataTemplate x:Key=\"FileGroupResultTemplate\"", 6600);
        Assert.Contains("Visibility=\"{x:Bind HasContentMatches, Mode=OneWay}\"", fileGroupTemplate);
        // A second pill distinguishes a file-name match from a content match (content-only, name-only, or both).
        Assert.Contains("Visibility=\"{x:Bind HasFileNameMatch, Mode=OneWay}\"", fileGroupTemplate);
        // The two pills stack VERTICALLY (matches on top, "file name" below) so a row with both a
        // content match and a file-name match shows the file-name pill under the match-count pill.
        AssertContainsInOrder(fileGroupTemplate,
            "<StackPanel Grid.Column=\"1\" Orientation=\"Vertical\"",
            "Visibility=\"{x:Bind HasContentMatches, Mode=OneWay}\"",
            "Visibility=\"{x:Bind HasFileNameMatch, Mode=OneWay}\"");
        // Both pills carry a leading FontIcon: a Document glyph for content matches, a Rename glyph for
        // the file-name match.
        Assert.Contains("<FontIcon Glyph=\"&#xE8A5;\" FontSize=\"10\" Foreground=\"#B0E0D0\"", fileGroupTemplate);
        Assert.Contains("<FontIcon Glyph=\"&#xE8AC;\" FontSize=\"10\" Foreground=\"#A9CBF2\"", fileGroupTemplate);
    }

    [Fact]
    public void FileNameOnlyPreview_RendersFullFileWithSelectedMatchTextButNoNavigation()
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
            "bool isMatchLine = isFileNameOnlyPreview || lineNum == r.LineNumber;");

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection", window: 5200);
        AssertContainsInOrder(concatenated,
            "bool isFileNameOnlyPreview = r.LineNumber <= 0;",
            "fullFile: isFileNameOnlyPreview",
            "bool isMatchLine = isFileNameOnlyPreview || lineNum == r.LineNumber;",
            "isFileNameOnlyPreview ? null : rx",
            "isMatchLine && !isFileNameOnlyPreview ? _matchParagraphs : null");

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

        string blockSurface = ExtractMethodWindow(MainWindowSource, "ShowPreviewBlockSurface", window: 1200);
        Assert.Contains("ApplyPreviewBlockContentBackground();", blockSurface);
        Assert.Contains("HideMatchNavPanel();", blockSurface);

        string ensureSectionsSurface = ExtractMethodWindow(MainWindowSource, "EnsureSectionsSurface", window: 1200);
        AssertContainsInOrder(ensureSectionsSurface,
            "ClearPreviewBlockContentBackground();",
            "if (PreviewSectionsPanel.Visibility == Visibility.Visible)");

        string blockBackground = ExtractMethodWindow(MainWindowSource, "ApplyPreviewBlockContentBackground", window: 900);
        AssertContainsInOrder(blockBackground,
            "CreatePreviewSectionContentBackgroundBrush(isActive: true)",
            "PreviewScrollViewer.Background = brush;",
            "grid.Background = brush;",
            "PreviewMessagePanel.Background = brush;");
    }

    [Fact]
    public void ReportExportDialog_ShowsTitleGlyphAndHidesNativeTitleBar()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ReportExportDialog.cs"));

        // The dialog shows its in-content title (with a glyph) but hides the OS title bar
        // (modal-no-title-bar rule). ShowTitle is intentionally NOT set to false here.
        AssertContainsInOrder(source,
            "Title = \"Export Report\",",
            "TitleGlyph =",
            "Content = root,",
            "PrimaryButtonText = \"Export\",",
            "CloseButtonText = \"Cancel\",",
            "ShowTitleBar = false,");
        Assert.DoesNotContain("ShowTitle = false", source);
    }

    [Fact]
    public void ActiveSearchPreview_BoundsDenseSingleLineInitialRender()
    {
        string buildHighlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 6000);

        AssertContainsInOrder(buildHighlight,
            "bool truncatePreviewLines = ViewModel.IsSearching",
            "? ShouldTruncateOverflowPreviewLines()",
            ": ShouldTruncateInitialPreviewLines();");

        AssertContainsInOrder(buildHighlight,
            "if (distinctMatchLines.Length == 1)",
            "if (section.Blocks.Count - startingBlocks >= maxBlocks)",
            "AddPreviewLineParagraphsAroundResult(",
            "targetOnlyMatchEntry: true,",
            "maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks));");

        string aroundResult = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphsAroundResult", window: 6000);
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
    public void PlainFileClicksDoNotPreviewButMatchLineTapsDo()
    {
        string itemClick = ExtractMethodWindow(MainWindowSource, "OnResultItemClick", window: 450);
        Assert.Contains("OnResultItemClick: no preview change", itemClick);
        Assert.DoesNotContain("UpdatePreviewAsync(g[0])", itemClick);
        Assert.DoesNotContain("TryScrollToPreviewSection(g[0].FilePath)", itemClick);
        Assert.DoesNotContain("OnFileGroupHeaderTapped", MainWindowSource);
        Assert.DoesNotContain("SelectFileGroupMatchesAndPreviewAsync(g, \"single click\")", MainWindowSource);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped", window: 1200);
        Assert.Contains("result.IsSelected = !result.IsSelected;", tapped);
        Assert.Contains("UpdateSelectionForMatchLine(result, nameof(OnMatchLineTapped));", tapped);
        Assert.Contains("OnMatchLineTapped: selection preview", tapped);
        Assert.Contains("await UpdatePreviewForMatchSelectionAsync(result);", tapped);
        Assert.DoesNotContain("UpdatePreviewAsync", tapped);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", tapped);
        Assert.DoesNotContain("EnsureCheckedMatchInPreviewAsync", tapped);
    }

    [Fact]
    public void ResultRows_ShowLineAndColumnForSameLineMatches()
    {
        string matchLineTemplate = ExtractXamlWindow("Tapped=\"OnMatchLineTapped\"", 3200);
        Assert.Contains("ToolTipService.ToolTip=\"{x:Bind LineLocationTooltip, Mode=OneWay}\"", matchLineTemplate);
        Assert.Contains("Text=\"{x:Bind LineLocationDisplay, Mode=OneWay}\"", matchLineTemplate);
        Assert.DoesNotContain("Text=\"{x:Bind LineNumber}\"", matchLineTemplate);
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
        string expanding = ExtractMethodWindow(MainWindowSource, "OnFileGroupExpanding", window: 2200);
        Assert.Contains("expand only", expanding);
        Assert.DoesNotContain("g.SelectAll();", expanding);
        Assert.DoesNotContain("SelectFileGroupMatches", expanding);
        Assert.DoesNotContain("TryScrollToPreviewSection", expanding);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", expanding);
    }

    [Fact]
    public void ResultsFileOverlay_ClickCollapsesMatchingDrawer()
    {
        string overlayXaml = ExtractXamlWindow("x:Name=\"ResultsFileOverlay\"", 1500);
        Assert.Contains("Tapped=\"OnResultsFileOverlayTapped\"", overlayXaml);
        Assert.Contains("DoubleTapped=\"OnResultsFileOverlayDoubleTapped\"", overlayXaml);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnResultsFileOverlayTapped", window: 700);
        Assert.Contains("CollapseResultsFileOverlayFromInput(e.OriginalSource as DependencyObject)", tapped);
        Assert.Contains("e.Handled = true;", tapped);

        string doubleTapped = ExtractMethodWindow(MainWindowSource, "OnResultsFileOverlayDoubleTapped", window: 700);
        Assert.Contains("CollapseResultsFileOverlayFromInput(e.OriginalSource as DependencyObject)", doubleTapped);

        string collapse = ExtractMethodWindow(MainWindowSource, "CollapseResultsFileOverlayFromInput", window: 1600);
        AssertContainsInOrder(collapse,
            "IsInsideHeaderCommand(originalSource, ResultsFileOverlay)",
            "return false;",
            "var group = _resultsFileOverlayGroup;",
            "if (group.IsExpanded)",
            "group.IsExpanded = false;",
            "HideResultsFileOverlay();",
            "QueueResultsFileOverlayUpdate();",
            "return true;");
        Assert.DoesNotContain("SelectFileGroupMatches", collapse);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", collapse);
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
    public void HideMatchNavPanel_PreservesSectionTrackingWhileSectionsRemain()
    {
        // Regression: a filename-only / binary match has zero navigable matches,
        // so the end of PrependPreviewSectionsForFilesAsync routes through
        // UpdateMatchNavPanel -> HideMatchNavPanel while that file's section is
        // still on screen. HideMatchNavPanel must NOT tear down the dedup map
        // (_expanderFilePaths) or the rest of the section-tracking state while
        // section expanders remain in PreviewSectionsPanel — otherwise the same
        // file can be added to the preview a second time (duplicate section).
        string hide = ExtractMethodWindow(MainWindowSource, "HideMatchNavPanel", window: 2600);

        const string guard = "if (!PreviewSectionsPanel.Children.OfType<Expander>().Any())";
        int guardIndex = hide.IndexOf(guard, StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "HideMatchNavPanel must guard section-tracking teardown behind a no-sections-remain check.");

        // The dedup map, expander caches, lazy/deferred queues and sticky header
        // are cleared only inside the guard.
        AssertContainsInOrder(hide,
            guard,
            "_lazySections.Clear();",
            "_deferredOrderedFiles = null;",
            "_expanderFilePaths.Clear();",
            "_expanderHeaderArgs.Clear();",
            "_blockExpanderCache.Clear();",
            "_stickyHeaderExpander = null;");

        // None of the section-tracking clears may run unconditionally (before the guard).
        string beforeGuard = hide[..guardIndex];
        Assert.DoesNotContain("_expanderFilePaths.Clear();", beforeGuard);
        Assert.DoesNotContain("_blockExpanderCache.Clear();", beforeGuard);
        Assert.DoesNotContain("_expanderHeaderArgs.Clear();", beforeGuard);
        Assert.DoesNotContain("_lazySections.Clear();", beforeGuard);
        Assert.DoesNotContain("_deferredOrderedFiles = null;", beforeGuard);
    }

    [Fact]
    public void LargeMatchPreview_BuildsSectionsOffTreeYieldsAndRegistersOverflow()
    {
        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync");
        Assert.Contains("addToPanel: false", prepend);
        Assert.Contains("await YieldLowAsync();", prepend);
        Assert.Contains("PreviewSectionsPanel.Children.Insert", prepend);

        // The method grows over time; size the window to comfortably reach its tail
        // (RegisterSectionOverflow sits ~15.3k chars past the signature).
        string highlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", 19000);
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
        Assert.Contains("bool truncatePreviewLines = ViewModel.IsSearching", highlight);
        Assert.Contains(": ShouldTruncateInitialPreviewLines();", highlight);
        string previewLineTruncation = ExtractMethodWindow(MainWindowSource, "ShouldTruncatePreviewLines", window: 400);
        Assert.Contains("PreviewWrapMode.NoWrap", previewLineTruncation);
        string initialPreviewLineTruncation = ExtractMethodWindow(MainWindowSource, "ShouldTruncateInitialPreviewLines", window: 500);
        Assert.Contains("ShouldTruncatePreviewLines()", initialPreviewLineTruncation);
        Assert.Contains("PreviewWrapMode.NoWrap", initialPreviewLineTruncation);
        Assert.Contains("maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks)", highlight);
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
        Assert.Contains("ShouldTruncateInitialPreviewLines()", overflowTruncation);

        string aroundResult = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphsAroundResult");
        Assert.Contains("bool truncate = true", aroundResult);
        Assert.Contains("bool isContinuationSegment = firstParagraph is not null;", aroundResult);
        Assert.Contains("bool useContinuationGutter = isContinuationSegment || continuationGutter;", aroundResult);
        Assert.Contains("truncationState: isContinuationSegment ? null : expansionState", aroundResult);
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
        Assert.Contains("ov.RenderedSoFar >= MaxOverflowRenderedPerSection", expandChunk);

        string expandScrollChunk = ExtractMethodWindow(MainWindowSource, "ExpandOverflowChunk");
        Assert.Contains("bool truncatePreviewLines = ShouldTruncateOverflowPreviewLines()", expandScrollChunk);
        Assert.Contains("ov.RenderedSoFar >= MaxOverflowRenderedPerSection", expandScrollChunk);
        Assert.Contains("MarkOverflowCeilingReached(section, ov);", expandScrollChunk);

        string overflowCeiling = ExtractMethodWindow(MainWindowSource, "MarkOverflowCeilingReached", window: 900);
        Assert.Contains("if (ov.CeilingReached) return;", overflowCeiling);
        Assert.Contains("ov.CeilingReached = true;", overflowCeiling);
        Assert.Contains("AppendOverflowCeilingNotice(section, ov.OriginalTotal, ov.RenderedSoFar)", overflowCeiling);

        string autoOverflow = ExtractMethodWindow(MainWindowSource, "TryAutoLoadOverflowOnScroll", window: 2200);
        Assert.Contains("IsOverflowAutoLoadSuppressedForMatchNavigation()", autoOverflow);
        Assert.Contains("ov.RenderedSoFar >= MaxOverflowRenderedPerSection", autoOverflow);

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
            "SectionNavLabel.Text = MatchNavMath.FormatSectionOccurrenceLabel(_activeSectionNav.CurrentIndex, total);");

        string mainWindowXaml = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
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
        string previewBuilder = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewBuilder.cs"));
        string selectionAutoScroll = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewSelectionAutoScroll.cs"));

        Assert.Contains("private sealed class PreviewTruncatedLineState", previewBuilder);
        Assert.Contains("private sealed record PreviewShowMoreAction", previewBuilder);
        Assert.Contains("_previewShowMoreEllipsisBrush = new(Microsoft.UI.Colors.DodgerBlue)", previewBuilder);
        AssertContainsInOrder(previewBuilder,
            "private int GetPreviewShowMoreMaxWindowLength()",
            "GetEffectiveSegmentSize() - (2 * LineTruncator.Ellipsis.Length)",
            "private int GetPreviewTruncatedLength()",
            "return Math.Min(configuredLength, GetPreviewShowMoreMaxWindowLength());");
        Assert.Contains("LogPreviewShowMoreDiagnostics(\"BuildHighlightSection\")", previewBuilder);

        string addParagraphs = ExtractMethodWindow(previewBuilder, "AddPreviewLineParagraphs", window: 3400);
        AssertContainsInOrder(addParagraphs,
            "TruncatePreviewLineAroundResult(line, result, rx) : TruncatePreviewLineWindow(line, rx)",
            "CreatePreviewTruncatedLineState(window, line, lineNum, isMatchLine, result, rx)",
            "truncationState: isContinuation ? null : expansionState");

        string textRuns = ExtractMethodWindow(previewBuilder, "AddPreviewTextRuns", window: 3200);
        AssertContainsInOrder(textRuns,
            "line.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)",
            "CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Prefix)",
            "AddPreviewTextSpanRuns(para, line[start..end], isMatchLine, rx, matchOrdinalsToColor, spanForThisText)",
            "CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Suffix)");

        string inline = ExtractMethodWindow(previewBuilder, "CreatePreviewShowMoreInline", window: 2600);
        Assert.Contains("private InlineUIContainer CreatePreviewShowMoreInline", inline);
        Assert.Contains("var marker = new TextBlock", inline);
        Assert.Contains("var markerHost = new Border", inline);
        Assert.Contains("Background = _transparentBrush", inline);
        Assert.Contains("Foreground = _previewShowMoreEllipsisBrush", inline);
        Assert.Contains("FontSize = ResolvePreviewShowMoreEllipsisFontSize()", inline);
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
        Assert.Contains("PreviewShowMoreTooltipCursorOverlapDip = 6", previewBuilder);

        string tooltip = ExtractMethodWindow(previewBuilder, "ShowPreviewShowMoreTooltip", window: 5800);
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
        Assert.Contains("GetPreviewShowMoreButtonCenterOffset(bubbleWidth)", tooltip);
        Assert.Contains("pointer.X - showMoreCenterOffset", tooltip);
        Assert.Contains("pointer.Y - (PreviewShowMoreTooltipBubble.Padding.Top + PreviewShowMoreTooltipCursorOverlapDip)", tooltip);
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
        Assert.Contains("HidePreviewShowMoreTooltipIfExpandedAway(action, mode)", expand);

        // After an expansion completes, the action panel must dismiss when the ellipsis
        // that triggered it is gone: "Show all" always (the whole line is revealed), and
        // "Show more" once its own edge's marker has been fully expanded away.
        string expandedAway = ExtractMethodWindow(previewBuilder, "HidePreviewShowMoreTooltipIfExpandedAway", window: 1600);
        Assert.Contains("PreviewShowMoreTooltipOverlay.Visibility != Visibility.Visible", expandedAway);
        Assert.Contains("ReferenceEquals(_previewShowMoreTooltipAction.State, action.State)", expandedAway);
        Assert.Contains("mode == PreviewShowMoreExpandMode.All", expandedAway);
        Assert.Contains("_previewShowMoreTooltipEdge == PreviewShowMoreEdge.Prefix && !prefixMarkerVisible", expandedAway);
        Assert.Contains("_previewShowMoreTooltipEdge == PreviewShowMoreEdge.Suffix && !suffixMarkerVisible", expandedAway);
        Assert.Contains("HidePreviewShowMoreTooltip()", expandedAway);

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
        // A "Show more"/"Show all" rebuild must preserve the originally-selected
        // occurrence's coloring restriction so newly revealed, unselected matches
        // stay context-colored instead of being painted as selected matches.
        Assert.Contains("BuildTargetColorOrdinals(state, window, window.Text, segmentDisplayStart: 0)", rebuild);
        Assert.Contains("AddPreviewTextRuns(paragraph, window.Text, state.IsMatchLine, state.Regex, state, matchOrdinalsToColor)", rebuild);
        string fullLine = ExtractMethodWindow(previewBuilder, "AddPreviewFullLineSegmentRuns", window: 2200);
        Assert.Contains("paragraph.Inlines.Add(new LineBreak())", fullLine);
        Assert.Contains("AddPreviewTextSpanRuns", fullLine);
        Assert.Contains("BuildTargetColorOrdinals(state, fullWindow, segmentText, segmentDisplayStart: index)", fullLine);
        string buildOrdinals = ExtractMethodWindow(previewBuilder, "BuildTargetColorOrdinals", window: 1400);
        Assert.Contains("if (!state.ColorTargetMatchOnly)", buildOrdinals);
        Assert.Contains("return null;", buildOrdinals);
        Assert.Contains("TryGetTargetMatchOrdinalInSegment(state.SourceLine, state.Result, state.Regex, window, segment, segmentDisplayStart, out int ordinal)", buildOrdinals);

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
        Assert.Contains("Text = \"       \"", helper);
        Assert.Contains("Foreground = s_gutterSepBrush", helper);
        // Idempotency must be derived from the rows actually present (LineBreak count),
        // not a side table keyed on object identity, otherwise a moved/re-synced gutter
        // paragraph gets a second full set of continuation rows and renders ~2x too tall.
        Assert.Contains("if (inline is LineBreak)", helper);
        Assert.Contains("currentContinuationRows > targetContinuationRows", helper);
        Assert.DoesNotContain("s_gutterWrappedContinuationCounts", helper);
    }

    [Fact]
    public void PreviewMatchColor_IsOnlyUsedForResultMatchLines()
    {
        string matchLineLoaded = ExtractMethodWindow(MainWindowSource, "OnMatchLineLoaded", window: 900);
        Assert.Contains("int matchStart = r.IsEvicted ? r.ShortPreviewMatchStart : r.MatchStartColumn;", matchLineLoaded);
        Assert.Contains("HighlightInline(para, r.MatchLine, matchStart, r.MatchLength);", matchLineLoaded);

        string textRuns = ExtractMethodWindow(MainWindowSource, "AddPreviewTextSpanRuns", window: 4400);
        Assert.Contains("s_previewSearchMatchRuns.AddOrUpdate(hit, new object());", textRuns);
        Assert.Contains("bool useMatchColor = isMatchLine && (matchOrdinalsToColor is null || matchOrdinalsToColor.Contains(matchOrdinal));", textRuns);
        Assert.Contains("hit.Foreground = _matchTextBrush;", textRuns);
        Assert.Contains("hit.Foreground = s_contextTextBrush;", textRuns);
        Assert.Contains("hit.Foreground = _matchLineBrush;", textRuns);

        string aroundResult = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphsAroundResult", window: 4200);
        Assert.Contains("HashSet<int>? matchOrdinalsToColor = null;", aroundResult);
        Assert.Contains("matchOrdinalsToColor.Add(colorOrdinal);", aroundResult);
        Assert.Contains("matchOrdinalsToColor: matchOrdinalsToColor", aroundResult);
        // The target-only coloring restriction must be carried on the expansion state
        // so a later "Show more"/"Show all" rebuild keeps coloring only the selected
        // occurrence instead of every regex hit in the newly revealed text.
        Assert.Contains("colorTargetMatchOnly: targetOnlyMatchEntry", aroundResult);

        string addParagraphs = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphs");
        Assert.Contains("if (!isMatchLine || matchParagraphs is null)", addParagraphs);
        Assert.Contains("AddMatchEntries(", addParagraphs);

        string matchRuns = ExtractMethodWindow(MainWindowSource, "IsSearchMatchRun", window: 900);
        Assert.Contains("s_previewSearchMatchRuns.TryGetValue(run, out _)", matchRuns);

        string selection = ExtractMethodWindow(MainWindowSource, "EnsureCheckedMatchInPreviewAsync", window: 3400);
        Assert.Contains("ApplyMatchColorToParagraphMatch(paragraph, matchInPara);", selection);
    }

    [Fact]
    public void MultilinePreview_HighlightsFullCrossLineSpan()
    {
        string previewBuilder = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewBuilder.cs"));

        // The per-line span geometry is delegated to the pure, unit-tested helper.
        string computeSpan = ExtractMethodWindow(previewBuilder, "ComputeMultilineLineSpan", window: 1200);
        Assert.Contains("if (!result.IsMultilineMatch || result.MatchEndLineNumber is not int endLine)", computeSpan);
        Assert.Contains("MultilinePreviewSpan.TryGetLineSpan(", computeSpan);

        // The forced-span branch colors the computed columns directly (a per-line regex can never
        // match a cross-line pattern), registering the run so it is navigable/boxable.
        string textSpanRuns = ExtractMethodWindow(previewBuilder, "AddPreviewTextSpanRuns", window: 1600);
        AssertContainsInOrder(textSpanRuns,
            "if (forcedSpan is (int forcedStart, int forcedEnd))",
            "s_previewSearchMatchRuns.AddOrUpdate(spanHit, new object());",
            "spanHit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;",
            "spanHit.Foreground = _matchTextBrush;");

        // Forced coloring only applies when the whole line renders as ONE untruncated segment so the
        // full-line columns map 1:1 to the rendered text.
        string addParagraphs = ExtractMethodWindow(previewBuilder, "AddPreviewLineParagraphs", window: 3000);
        Assert.Contains("bool singleSegment = window.Text.Length <= GetEffectiveSegmentSize();", addParagraphs);
        Assert.Contains("window.SourceStart == 0 && window.SourceEnd >= line.Length", addParagraphs);
        Assert.Contains("forcedSpan: isContinuation ? null : effectiveForcedSpan", addParagraphs);

        // The line window extends to the span END line so every spanned line is available to color.
        string getPreviewLines = ExtractMethodWindow(previewBuilder, "GetPreviewLines", window: 2400);
        Assert.Contains("int spanEndLine = r.MatchEndLineNumber ?? matchLineNum;", getPreviewLines);
        Assert.Contains("endLine = Math.Min(allLines.Length - 1, (spanEndLine - 1) + previewLines);", getPreviewLines);

        // The single-match highlight branch renders the full span and colors each line via forcedSpan.
        string highlight = ExtractMethodWindow(previewBuilder, "BuildHighlightSectionAsync", window: 17000);
        Assert.Contains("bool multilineMatch = matchResult.IsMultilineMatch;", highlight);
        Assert.Contains("int spanEndLine = matchResult.MatchEndLineNumber ?? matchLineNumber;", highlight);
        Assert.Contains("forcedSpan: ComputeMultilineLineSpan(matchResult, lineNum, allLines[i].Length)", highlight);
        // The multi-range branch colors body/end lines that are not any result's start line.
        Assert.Contains("(int start, int end)? SpanForLine(int lineNumber, int lineLength)", highlight);
        Assert.Contains("var forcedSpan = SpanForLine(lineNum, allLines[i].Length);", highlight);

        // The stored-context branch (on-disk file couldn't be re-read, allLines == null) must ALSO
        // apply forced-span coloring for cross-line matches — otherwise the matched columns render
        // uncolored because a per-line regex can never match a `a\nb` pattern within one line.
        // The start line uses stored DISPLAY-space columns (its text is the possibly-truncated
        // MatchLine); body/end lines use the source-column span against their full stored text.
        AssertContainsInOrder(highlight,
            "var storedMultilineResults = cappedResults",
            ".Where(result => result.IsMultilineMatch && result.MatchEndLineNumber is int)",
            "(int start, int end)? StoredSpanForLine(int lineNumber, string lineTextForLine)",
            "if (lineNumber == result.LineNumber)",
            "int s = Math.Max(0, result.MatchStartColumn);",
            "int e = s + Math.Max(0, result.MatchLength);",
            "var span = ComputeMultilineLineSpan(result, lineNumber, lineTextForLine.Length);",
            "var forcedSpan = StoredSpanForLine(lineNum, lineText[lineNum]);",
            "truncate: truncatePreviewLines && forcedSpan is null,",
            "forcedSpan: forcedSpan);");
    }

    [Fact]
    public void MultilinePreview_ActiveMatchOverlayBoxesEverySpannedLine()
    {
        string matchNav = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.MatchNav.cs"));

        // The active-match overlay must box the matched portion on EVERY spanned line of a cross-line
        // match (not just the start line), reusing the extra-word-marker mechanism.
        string overlay = ExtractMethodWindow(matchNav, "TryUpdateActiveMatchOverlayFromActualRun", window: 23200);
        Assert.Contains("TryAddMultilineSpanActiveMarkers(block, targetPara, markerHeight, viewportTop, viewportBottom, viewportWidth, charWidth, markerTopDelta)", overlay);

        string spanMarkers = ExtractMethodWindow(matchNav, "TryAddMultilineSpanActiveMarkers", window: 4600);
        // Only cross-line matches, and only the OTHER spanned lines (start line handled by the primary marker).
        Assert.Contains("!result.IsMultilineMatch", spanMarkers);
        Assert.Contains("result.MatchEndLineNumber is not int endLine", spanMarkers);
        Assert.Contains("ReferenceEquals(para, startPara)", spanMarkers);
        Assert.Contains("lineNumber <= startLine", spanMarkers);
        Assert.Contains("lineNumber > endLine", spanMarkers);
        // Geometry is derived from the span's end column (not from a registered gold run), so the box
        // appears even if the line rendered without forced-span coloring.
        Assert.Contains("result.MatchEndColumn ?? contentLength", spanMarkers);
        Assert.Contains("GetPreviewParagraphTextPointerAtIndex(para, gutterLength + spanEndColumn)", spanMarkers);
        Assert.Contains("_activeMatchExtraWordMarkers.Add(marker);", spanMarkers);
        // Off-screen spanned lines are skipped gracefully.
        Assert.Contains("top + height <= viewportTop || top >= viewportBottom", spanMarkers);
    }

    [Fact]
    public void PhantomMatchParagraph_RecoversUnregisteredRunsBeforeColoringAndBoxing()
    {
        // A resolved match paragraph can carry a navigation entry while its match Run was
        // rendered without registration in s_previewSearchMatchRuns (e.g. an unselected
        // sibling occurrence inside a sibling's context window). Both the coloring path and
        // the overlay-boxing path must recover run recognition from the live search regex so
        // the active-match overlay can be positioned and the run colored gold.
        string recover = ExtractMethodWindow(MainWindowSource, "TryRecoverUnregisteredMatchRuns", window: 2400);
        Assert.Contains("BuildSearchHighlightRegex()", recover);
        Assert.Contains("foreach (Match m in rx.Matches(paragraphText))", recover);
        // Registration restores recognition only for runs fully inside a regex match span,
        // and never colors them (unselected siblings stay visually plain).
        Assert.Contains("start >= matchStart && start + length <= matchEnd", recover);
        Assert.Contains("s_previewSearchMatchRuns.AddOrUpdate(run, new object());", recover);
        Assert.Contains("_paragraphMatchRunCache.Remove(para);", recover);

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyMatchColorToParagraphMatch", window: 900);
        Assert.Contains("if (TryRecoverUnregisteredMatchRuns(para))", apply);

        string boxMatch = ExtractMethodWindow(MainWindowSource, "BoxMatchRun", window: 3000);
        AssertContainsInOrder(boxMatch,
            "if (TryRecoverUnregisteredMatchRuns(para))",
            "matches = GetMatchRunsForParagraph(para);",
            "NO BOXABLE RUN");
    }

    [Fact]
    public void PreviewFindHighlighter_UsesPreviewFindColorAndCrlfOffsets()
    {
        string highlight = ExtractMethodWindow(MainWindowSource, "HighlightFindMatchInPreviewBlock", window: 2600);
        Assert.Contains("int searchParaLen = paraLen + 2; // paragraph text + \\r\\n", highlight);
        Assert.Contains("blockTextLen += searchParaLen;", highlight);
        Assert.DoesNotContain("blockTextLen += paraLen + 1", highlight);

        string map = ExtractMethodWindow(MainWindowSource, "MapSearchOffsetToBlockOffset", window: 1200);
        Assert.Contains("searchPos += paraLen + 2; // \\r\\n", map);
        Assert.Contains("blockPos += paraLen + 2;  // \\r\\n", map);
        Assert.DoesNotContain("blockPos += paraLen + 1", map);

        string apply = ExtractMethodWindow(MainWindowSource, "ApplyFindHighlighter", window: 1200);
        Assert.Contains("Windows.UI.Color.FromArgb(130, 64, 156, 255)", apply);
        Assert.DoesNotContain("Windows.UI.Color.FromArgb(180, 255, 185, 0)", apply);
    }

    [Fact]
    public void LineCheckboxClick_AddsCheckedMatchContextToPreview()
    {
        Assert.Contains("Click=\"OnMatchLineCheckBoxClicked\"", MainWindowXaml);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped");
        Assert.Contains("e.OriginalSource is DependencyObject source && IsInsideButton(source)", tapped);
        Assert.Contains("OnMatchLineTapped: selection preview", tapped);
        Assert.Contains("await UpdatePreviewForMatchSelectionAsync(result);", tapped);

        string checkboxClicked = ExtractMethodWindow(MainWindowSource, "OnMatchLineCheckBoxClicked", window: 1000);
        Assert.Contains("result.IsSelected = isChecked;", checkboxClicked);
        Assert.Contains("UpdateSelectionForMatchLine(result, nameof(OnMatchLineCheckBoxClicked));", checkboxClicked);
        Assert.Contains("await UpdatePreviewForMatchSelectionAsync(result);", checkboxClicked);

        string updatePreviewForSelection = ExtractMethodWindow(MainWindowSource, "UpdatePreviewForMatchSelectionAsync", window: 2200);
        Assert.Contains("if (result.IsSelected)", updatePreviewForSelection);
        Assert.Contains("if (!ViewModel.MatchLineCheckAddsToPreview) return;", updatePreviewForSelection);
        Assert.Contains("await EnsureCheckedMatchInPreviewAsync(result);", updatePreviewForSelection);
        Assert.DoesNotContain("await UpdateMultiSelectPreviewAsync(result);", updatePreviewForSelection);
        Assert.DoesNotContain("UpdatePreviewAsync", checkboxClicked);

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
            "await PrependPreviewSectionsForFilesAsync(newFiles, result.FilePath, result);",
            "RefreshPreviewSectionHeaderForSelectedMatches(result.FilePath);",
            "return;",
            "await RevealCheckedMatchInPreviewSectionAsync(result);",
            "RefreshPreviewSectionHeaderForSelectedMatches(result.FilePath);");

        string refreshHeader = ExtractMethodWindow(MainWindowSource, "RefreshPreviewSectionHeaderForSelectedMatches", window: 3000);
        AssertContainsInOrder(refreshHeader,
            "if (!TryFindPreviewSection(filePath, out var expander, out var section))",
            "var selectedForFile = ViewModel.GetAllSelectedResults()",
            ".Where(result => string.Equals(result.FilePath, filePath, StringComparison.OrdinalIgnoreCase))",
            ".ToList();",
            "string detail = $\"{selectedForFile.Count:N0} selected match(es)\";",
            "expander.Header = BuildPreviewSectionHeader(filePath, detail, section, selectedForFile);",
            "_expanderHeaderArgs[expander] = (filePath, detail, section, selectedForFile);");
        AssertContainsInOrder(refreshHeader,
            "if (ReferenceEquals(_stickyHeaderExpander, expander))",
            "StickyFileHeader.Child = BuildPreviewSectionHeader(filePath, detail, section, selectedForFile);");

        string revealChecked = ExtractMethodWindow(MainWindowSource, "RevealCheckedMatchInPreviewSectionAsync", window: 5000);
        AssertContainsInOrder(revealChecked,
            "await MaterializeLazySectionAsync(section);",
            "if (!TryFindPreviewMatchParagraph(section, result, out var paragraph, out var matchInPara))",
            "await AppendCheckedMatchContextAsync(section, result);",
            "ReorderMatchParagraphsToPreviewSectionOrder();");
        Assert.Contains("SetCurrentMatchToMatch(section, paragraph, matchInPara);", revealChecked);
        Assert.Contains("ScrollPreviewToLine(section, paragraph);", revealChecked);

        string appendContext = ExtractMethodWindow(MainWindowSource, "AppendCheckedMatchContextAsync", window: 10000);
        Assert.Contains("int previewLines = ViewModel.PreviewContextLines;", appendContext);
        Assert.Contains("ReadAllLinesWithEncodingSync(result.FilePath)", appendContext);
        Assert.Contains("var lines = GetPreviewLines(result, allLines, previewLines, fullFile: false);", appendContext);
        Assert.Contains("bool isMatchLine = lineNum == result.LineNumber;", appendContext);
        Assert.Contains("AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, result, rx, truncate: truncatePreviewLines", appendContext);
    }

    [Fact]
    public void StaleCheckedLine_FallsBackToStoredMatchContextAndRevealsTargetAfterPrepend()
    {
        string buildHighlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 1800);
        AssertContainsInOrder(buildHighlight,
            "if (allLines is not null && !HasReadablePreviewLine(results, allLines))",
            "using stored match context",
            "allLines = null;");

        string getPreviewLines = ExtractMethodWindow(MainWindowSource, "GetPreviewLines", window: 2400);
        Assert.Contains("if (fullFile && allLines is { Length: > 0 })", getPreviewLines);
        Assert.Contains("allLines is { Length: > 0 } && matchLineNum >= 1 && matchLineNum <= allLines.Length", getPreviewLines);
        Assert.DoesNotContain("if (matchIdx >= allLines.Length) matchIdx = allLines.Length - 1;", getPreviewLines);
        Assert.Contains("lines.Add((r.MatchLine, matchLineNum));", getPreviewLines);

        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync", window: 14000);
        Assert.Contains("SearchResult? scrollTarget = null", prepend);
        AssertContainsInOrder(prepend,
            "if (scrollTarget is not null)",
            "await RevealCheckedMatchInPreviewSectionAsync(scrollTarget);",
            "else if (scrollToFile is not null && PreviewSectionExists(scrollToFile))");

        int scrollBlockIndex = prepend.IndexOf("// Scroll to the target file or, for a checked match, to the exact line.", StringComparison.Ordinal);
        Assert.True(scrollBlockIndex >= 0);
        string scrollBlock = prepend[scrollBlockIndex..];
        AssertContainsInOrder(scrollBlock,
            "if (scrollTarget is not null)",
            "DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>",
            "await RevealCheckedMatchInPreviewSectionAsync(scrollTarget);");
    }

    [Fact]
    public void ImagePreview_RendersRecognizedOcrTextAndThumbnailInsteadOfRawBytes()
    {
        // Both low-level preview readers must short-circuit image files to their recognized
        // OCR text so the sections surface renders line numbers + match highlight + the
        // active-match box instead of the raw binary bytes. An empty array (no cached OCR
        // yet) safely falls back to the result's stored match context downstream.
        string readSync = ExtractMethodWindow(MainWindowSource, "ReadAllLinesWithEncodingSync", window: 600);
        Assert.Contains("private string[] ReadAllLinesWithEncodingSync(string filePath)", readSync);
        AssertContainsInOrder(readSync,
            "if (IsImagePreviewPath(filePath))",
            "return GetPreviewOcrLines(filePath) ?? Array.Empty<string>();");
        Assert.DoesNotContain("private static string[] ReadAllLinesWithEncodingSync", MainWindowSource);

        string readAsync = ExtractMethodWindow(MainWindowSource, "ReadAllLinesWithEncodingAsync", window: 600);
        Assert.Contains("private async Task<string[]> ReadAllLinesWithEncodingAsync(string filePath)", readAsync);
        AssertContainsInOrder(readAsync,
            "if (IsImagePreviewPath(filePath))",
            "return GetPreviewOcrLines(filePath) ?? Array.Empty<string>();");

        // The batch reader must be an instance method so it can resolve OCR text per image.
        Assert.Contains("private async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(", MainWindowSource);
        Assert.DoesNotContain("private static async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(", MainWindowSource);

        // The preview OCR cache is eagerly constructed so the background read threads can
        // read it without a lazy-init race.
        Assert.Contains("private readonly Yagu.Services.Ocr.OcrTextCache _previewOcrTextCache = new();", MainWindowSource);
        Assert.DoesNotContain("_previewOcrTextCache ??= new", MainWindowSource);

        // Each image renders its own thumbnail inside its section drawer, directly above the
        // recognized OCR text, so multiple image previews each show their own picture instead
        // of a single shared thumbnail floating above every drawer.
        string addSection = ExtractMethodWindow(MainWindowSource, "AddPreviewSection", window: 6000);
        AssertContainsInOrder(addSection,
            "if (IsImagePreviewPath(filePath))",
            "var imageBorder = CreateSectionImageThumbnail(filePath);",
            "Grid.SetColumnSpan(imageBorder, 2);");

        string sectionThumb = ExtractMethodWindow(MainWindowSource, "CreateSectionImageThumbnail", window: 4000);
        Assert.Contains("LoadSectionImageThumbnailAsync(image, path)", sectionThumb);
        Assert.DoesNotContain("await UpdateSectionsImageThumbnailAsync();", MainWindowSource);

        // The thumbnail is centered in the drawer and carries an expand button that opens the
        // full-resolution original in a closable, resizable modal. The button uses a constant
        // dark chip + white glyph so it stays visible on any image behind it.
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Center", sectionThumb);
        Assert.Contains("ShowFullSizeImageModalAsync(capturedPath)", sectionThumb);
        Assert.Contains("[\"ButtonBackground\"] = chipBackground", sectionThumb);
        string fullSize = ExtractMethodWindow(MainWindowSource, "ShowFullSizeImageModalAsync", window: 2000);
        Assert.Contains("YaguDialog.ShowAsync(", fullSize);
        Assert.Contains("ShowTopRightCloseButton = true", fullSize);
        Assert.Contains("IsResizable = true", fullSize);

        // The single-file block surface still shows its picture above the recognized text.
        Assert.Contains("x:Name=\"PreviewImageThumbnailBorder\" Grid.Row=\"0\"", MainWindowXaml);
        Assert.Contains("x:Name=\"PreviewSectionsPanel\" Grid.Row=\"1\"", MainWindowXaml);
    }

    [Fact]
    public void PreviewGapIndicator_OnlyAppearsBetweenNonContiguousLineRanges()
    {
        string helper = ExtractMethodWindow(MainWindowSource, "ShouldAddGapBetweenRenderedLines", window: 500);
        AssertContainsInOrder(helper,
            "previousLineNumber > 0",
            "nextLineNumber > 0",
            "nextLineNumber - previousLineNumber > 1");

        string appendContext = ExtractMethodWindow(MainWindowSource, "AppendCheckedMatchContextAsync", window: 12000);
        Assert.Contains("ShouldAddGapBetweenRenderedLines(lines.Max(l => l.lineNum), firstRenderedLine)", appendContext);
        Assert.Contains("ShouldAddGapBetweenRenderedLines(lastRenderedLine, lines.Min(l => l.lineNum))", appendContext);
        Assert.DoesNotContain("Append after existing content with gap indicator.", appendContext);

        string appendWindows = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows", window: 9000);
        Assert.Contains("ShouldAddGapBetweenRenderedLines(lastRenderedLine, start + 1)", appendWindows);

        string buildInitial = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 18000);
        AssertContainsInOrder(buildInitial,
            "int previousRangeEndLine = 0;",
            "ShouldAddGapBetweenRenderedLines(previousRangeEndLine, start + 1)",
            "previousRangeEndLine = end + 1;");

        string showMulti = ExtractMethodWindow(MainWindowSource, "ShowMultiHighlightPreviewAsync", window: 22000);
        AssertContainsInOrder(showMulti,
            "int previousRangeEndLine = 0;",
            "ShouldAddGapBetweenRenderedLines(previousRangeEndLine, start + 1)",
            "previousRangeEndLine = end + 1;");
    }

    [Fact]
    public void PreviewStoredContextWindows_AreMergedSoLineNumbersAreNotRepeated()
    {
        // When a file can't be re-read (allLines == null, e.g. OCR image fallback), each match's
        // stored ±context window is merged into a single ordered, de-duplicated map of line number
        // -> text, so overlapping windows no longer re-render (repeat) the same line numbers.
        string buildHighlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync", window: 18000);
        AssertContainsInOrder(buildHighlight,
            "var lineText = new Dictionary<int, string>();",
            "var matchResultByLine = new Dictionary<int, SearchResult>();",
            "if (lineNum == r.LineNumber)",
            "else if (!lineText.ContainsKey(lineNum))",
            "foreach (int lineNum in lineText.Keys.OrderBy(n => n))",
            "ShouldAddGapBetweenRenderedLines(previousLineNum, lineNum)",
            "isMatchLine ? _matchParagraphs : null",
            "storedContextCoveredResults = cappedResults.Count(r => renderedLineSet.Contains(r.LineNumber));");
    }

    [Fact]
    public void AppendHighlightMatchWindows_SkipsAlreadyRenderedLinesSoNumbersAreNotRepeated()
    {
        // Overflow expansion for highlight previews (e.g. dense same-line OCR matches) must not
        // re-render a line that is already on screen, otherwise the same line numbers repeat down
        // the pane. The window ranges are also sorted before merging so out-of-order pending
        // results can't produce overlapping windows. The per-occurrence overflow branch (when every
        // pending result shares an already-rendered line) dedups by SOURCE-COLUMN window membership
        // rather than by line number, so each occurrence outside a rendered window still gets a
        // dedicated nav target; the multi-line ranges path keeps the line-number guard.
        string append = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows", window: 9000);
        AssertContainsInOrder(append,
            "var sectionRenderedLines = GetRenderedLineNumbers(section);",
            "foreach (var range in rawRanges.OrderBy(r => r.start))",
            "if (IsSourceColumnWithinRenderedWindow(section, result.LineNumber, sourceColumn))",
            "if (!sectionRenderedLines.Add(lineNumber))");
    }

    [Fact]
    public void IsSourceColumnWithinRenderedWindow_TreatsOnlyOccurrencesInsideARenderedWindowAsCovered()
    {
        // The helper backing the same-line overflow dedup. An occurrence counts as already navigable
        // only when its source column actually falls inside a rendered window for that line — or the
        // full untruncated line was drawn (no recorded window). A negative column is never covered.
        // This is what lets a single very long source line page through every occurrence instead of
        // stalling at the first window.
        string helper = ExtractMethodWindow(MainWindowSource, "IsSourceColumnWithinRenderedWindow", window: 1400);
        AssertContainsInOrder(helper,
            "if (sourceColumn < 0)",
            "return false;",
            "foreach (var block in section.Blocks)",
            "block is not Paragraph para",
            "!s_paragraphLineNumbers.TryGetValue(para, out var lineObj)",
            "lineObj is not int renderedLine",
            "renderedLine != lineNumber",
            "if (!TryGetParagraphMatchWindow(para, out int windowStart, out int windowEnd))",
            "return true;",
            "if (sourceColumn >= windowStart && sourceColumn < windowEnd)",
            "return true;",
            "return false;");
    }

    [Fact]
    public void AppendHighlightMatchWindows_SameLineOverflow_GivesEachUncoveredOccurrenceItsOwnNavTarget()
    {
        // When every pending result is on an already-rendered (single very long) line, occurrences
        // whose source column is NOT inside a rendered window must each get a dedicated, sibling-
        // clipped window with its own match-nav entry — otherwise next/prev stays stuck at the first
        // occurrence. The branch prefers SourceMatchStartColumn (the column in the ORIGINAL line) and
        // falls back to MatchStartColumn, then renders a target-only window beside the existing line.
        string append = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows", window: 9000);
        AssertContainsInOrder(append,
            "int sourceColumn = result.SourceMatchStartColumn >= 0",
            "? result.SourceMatchStartColumn",
            ": result.MatchStartColumn;",
            "if (IsSourceColumnWithinRenderedWindow(section, result.LineNumber, sourceColumn))",
            "consumedResults++;",
            "AddPreviewLineParagraphsAroundResult(",
            "targetOnlyMatchEntry: true,",
            "MoveAppendedPreviewLineBesideExistingLine(");
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
            "_currentMatchIndex = -1;",
            "_ = GoToNextMatchAsync();");
        AssertContainsInOrder(updateMatchNav,
            "if (_activeMatchHighlight is not null)",
            "var (block, para, _) = _matchParagraphs[activeIndex];",
            "ScrollPreviewToLine(block, para);");
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
            "ChangePreviewViewForMatchNavigation(null, paragraphOffset)",
            "mode=actual-paragraph",
            "double lineHeight = EstimatePreviewLineHeight(block)");

        string actual = ExtractMethodWindow(MainWindowSource, "TryGetPreviewParagraphTargetVerticalOffset", window: 2800);
        AssertContainsInOrder(actual,
            "TryGetPreviewParagraphLineRect(targetPara",
            "block.TransformToVisual(PreviewScrollViewer)",
            "targetLineTop + markerHeight / 2 - viewportHeight / 2",
            "Math.Clamp(candidate, 0, PreviewScrollViewer.ScrollableHeight)");
    }

    [Fact]
    public void ActiveMatchOverlay_UsesActualOverlayCoordinatesAndWaitsForSettledScroll()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 24000);
        string transform = ExtractMethodWindow(MainWindowSource, "TransformRunRectToOverlay");
        Assert.Contains("TransformToVisual(ActiveMatchOverlay)", transform);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, rect)", updateOverlay);
        Assert.Contains("IsPreviewSectionBodySettledForActiveOverlay(block, out var layoutReason)", updateOverlay);
        Assert.Contains("ActiveMatchOverlay.Visibility = Visibility.Visible;", updateOverlay);
        Assert.Contains("targetRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward)", updateOverlay);
        Assert.Contains("usedEndRect", updateOverlay);
        Assert.Contains("TryGetEstimatedWrappedMatchPoint(", updateOverlay);
        Assert.Contains("ShouldUseEstimatedWrappedMatchPoint(", updateOverlay);
        Assert.Contains("TryBuildMeasuredWrappedActiveMatchMarkerRects(", updateOverlay);
        Assert.Contains("TryBuildWrappedActiveMatchMarkerRects(", updateOverlay);
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
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 24000);

        AssertContainsInOrder(updateOverlay,
            "var point = TransformRunRectToOverlay(block, targetPara, rect);",
            "if (ViewModel.PreviewWordWrap)",
            "bool hasEstimatedPoint = TryGetEstimatedWrappedMatchPoint(",
            "&& ShouldUseEstimatedWrappedMatchPoint(",
            "usedWrappedPointEstimate = true;",
            "if (usedWrappedPointEstimate && TryBuildWrappedActiveMatchMarkerRects(",
            "else if (!usedWrappedPointEstimate && TryBuildMeasuredWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = measuredMarkerRects;",
            "else if (!measuredEndSameRow && TryBuildWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = estimatedMarkerRects;");
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
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 24000);
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");
        string estimateDecision = ExtractMethodWindow(MainWindowSource, "ShouldUseEstimatedWrappedMatchPoint", window: 2200);

        AssertContainsInOrder(updateOverlay,
            "bool hasEstimatedPoint = TryGetEstimatedWrappedMatchPoint(",
            "&& ShouldUseEstimatedWrappedMatchPoint(",
            "point = estimatedPoint;",
            "usedWrappedPointEstimate = true;");
        AssertContainsInOrder(estimate,
            "double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;",
            "double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;",
            "double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);",
            "estimatedPoint = new Windows.Foundation.Point(correctedLeft, expectedTop);");
        AssertContainsInOrder(estimateDecision,
            "GetPreviewTextOverlayBounds(block, viewportWidth, out double textLeft, out double textRight);",
            "if (actualPoint.X < textLeft - tolerance || actualPoint.X > textRight + tolerance)",
            "return true;");
    }

    [Fact]
    public void WrappedLongActiveMatchOverlay_UsesMultipleWordMarkers()
    {
        Assert.Contains("private readonly List<Border> _activeMatchExtraWordMarkers = new();", MainWindowSource);

        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 24000);
        AssertContainsInOrder(updateOverlay,
            "bool hasEstimatedPoint = TryGetEstimatedWrappedMatchPoint(",
            "&& ShouldUseEstimatedWrappedMatchPoint(",
            "usedWrappedPointEstimate = true;",
            "if (usedWrappedPointEstimate && TryBuildWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = estimatedMarkerRectsFromPoint;",
            "else if (!usedWrappedPointEstimate && TryBuildMeasuredWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = measuredMarkerRects;",
            "else if (!measuredEndSameRow && TryBuildWrappedActiveMatchMarkerRects(",
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

        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 3000);
        AssertContainsInOrder(updateOverlay,
            "if (PreviewScrollViewer.Visibility != Visibility.Visible)",
            "return false;",
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
    public void ActiveOverlayRetryExhaustion_DoesNotForceNativeGeometryRead()
    {
        string queuedUpdate = ExtractMethodWindow(MainWindowSource, "QueueActiveMatchOverlayUpdate", window: 3600);
        Assert.Contains("IsActiveMatchGeometryTargetAttached(block, targetPara, targetRun)", queuedUpdate);
        Assert.DoesNotContain("targetRun.ElementStart?.GetCharacterRect", queuedUpdate);
        Assert.DoesNotContain("ScrollPreviewToLine(block, targetPara, forceCenter: true)", queuedUpdate);
        AssertContainsInOrder(queuedUpdate,
            "else if (IsRequestCurrent())",
            "stale WinUI TextPointers can fail-fast",
            "HideActiveMatchOverlay();");

        string attached = ExtractMethodWindow(MainWindowSource, "IsActiveMatchGeometryTargetAttached", window: 1200);
        AssertContainsInOrder(attached,
            "if (PreviewScrollViewer.Visibility != Visibility.Visible)",
            "if (!block.Blocks.Contains(targetPara))",
            "foreach (var inline in targetPara.Inlines)",
            "ReferenceEquals(inline, targetRun)");

        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 4000);
        AssertContainsInOrder(updateOverlay,
            "if (!IsActiveMatchGeometryTargetAttached(block, targetPara, targetRun))",
            "return false;",
            "var rect = targetRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);");
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
    public void NoWrapOverlay_RetriesFullyOffscreenAndClipsPartiallyOffscreenMarker()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 24000);

        AssertContainsInOrder(updateOverlay,
            "bool markerFullyOutside = point.X + markerWidth <= 0 || point.X >= viewportWidth;",
            "if (!ViewModel.PreviewWordWrap && markerFullyOutside)",
            "if (retryIfCenterRejected)",
            "ScrollMatchHorizontallyIntoView(block, targetPara);",
            "rejecting horizontally offscreen marker",
            "return false;",
            "double clippedMarkerLeft = Math.Max(point.X, 0);",
            "double clippedMarkerRight = Math.Min(point.X + markerWidth, viewportWidth);",
            "double visibleMarkerWidth = Math.Max(0, clippedMarkerRight - clippedMarkerLeft);");
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
    public void OpeningFullFileEditor_CancelsPendingMatchNavigationToAvoidNativeAccessViolation()
    {
        // Double-tapping a match opens the full-file editor, which collapses the
        // preview surface. Any match-nav scroll/overlay retries queued by the last
        // Next/Prev navigation must be cancelled, otherwise the deferred callbacks
        // fire against the collapsed PreviewScrollViewer and detached preview runs
        // and fault inside native XAML (access violation in Microsoft.UI.Xaml.dll).

        // The shared cancellation helper performs the full invalidation triad.
        string cancel = ExtractMethodWindow(MainWindowSource, "CancelPendingPreviewMatchNavigation", 600);
        AssertContainsInOrder(cancel,
            "InvalidatePendingMatchScrolls();",
            "_activeMatchOverlayUpdateRequestId++;",
            "HideActiveMatchOverlay();");

        // Opening the editor must invoke that cancellation, not just hide the overlay.
        string setVisible = ExtractMethodWindow(PreviewEditorSource, "SetPreviewEditorVisible", 1500);
        Assert.Contains("CancelPendingPreviewMatchNavigation();", setVisible);

        // The manual-scroll path (which never reproduced the crash) shares the
        // exact same cancellation, so the two surface-swap paths cannot drift.
        string manualScroll = ExtractMethodWindow(MainWindowSource, "NotePreviewManualScrollInput", 600);
        Assert.Contains("CancelPendingPreviewMatchNavigation();", manualScroll);

        // Defensive depth: the deferred chokepoints that touch native preview
        // geometry must bail while the surface is collapsed.
        string changeView = ExtractMethodWindow(MainWindowSource, "ChangePreviewViewForMatchNavigation", 900);
        AssertContainsInOrder(changeView,
            "if (PreviewScrollViewer.Visibility != Visibility.Visible)",
            "return false;",
            "PreviewScrollViewer.ChangeView(horizontalOffset, verticalOffset, zoomFactor, disableAnimation);");

        string overlayFromRun = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", 800);
        AssertContainsInOrder(overlayFromRun,
            "if (PreviewScrollViewer.Visibility != Visibility.Visible)",
            "return false;");
    }

    [Fact]
    public void LargeChunkedPreviewEditor_AutoAppendsOnScrollWithoutPromptMitigation()
    {
        Assert.Contains("private const long PreviewEditorChunkByteLength = 10L * 1024 * 1024;", PreviewEditorSource);

        string showChunked = ExtractMethodWindow(PreviewEditorSource, "ShowChunkedPreviewEditorAsync");
        AssertContainsInOrder(showChunked,
            "var wrapDecision = await ResolvePreviewEditorWrapAsync(result.FilePath, chunk.MaxLineLength, chunkSingleLine);",
            "_previewEditorWrapOverride = wrapDecision.wrapOverride;",
            "ApplyPreviewEditorWordWrap(wrapDecision.wrap);");
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
            "var wrapDecision = await ResolvePreviewEditorWrapAsync(result.FilePath, document.MaxLineLength, singleLine);",
            "_previewEditorWrapOverride = wrapDecision.wrapOverride;",
            "ApplyPreviewEditorWordWrap(wrapDecision.wrap);",
            "ShowFullFileEditorAsync: long line, wrap=");

        string applyEditorWrap = ExtractMethodWindow(PreviewEditorSource, "ApplyPreviewEditorWordWrap");
        Assert.Contains("PreviewEditor.WordWrap = wrap;", applyEditorWrap);
        Assert.Contains("PreviewEditor.UpdateLayout();", applyEditorWrap);

        string applyWrap = ExtractMethodWindow(MainWindowSource, "ApplyWordWrap");
        Assert.Contains("ApplyPreviewEditorWordWrap(_previewEditorWrapOverride ?? wrap);", applyWrap);
    }

    [Fact]
    public void LongLineWarning_DontRemindMeAgain_PersistsChoiceAndHonorsSavedPreference()
    {
        // The dialog offers the opt-out checkbox and reports whether it was checked.
        string prompt = ExtractMethodWindow(PreviewEditorSource, "PromptPreviewEditorLongLineWrapAsync");
        Assert.Contains("Don't remind me again", prompt);
        Assert.Contains("return (choice, dontRemindCheckBox.IsChecked == true);", prompt);

        // The resolver skips the prompt when a preference is saved, and persists the chosen button
        // when "Don't remind me again" was checked.
        string resolve = ExtractMethodWindow(PreviewEditorSource, "ResolvePreviewEditorWrapAsync");
        AssertContainsInOrder(resolve,
            "switch (ViewModel.PreviewLongLineWarningIndex)",
            "case 1: return (true, false, (bool?)false);",
            "case 2: return (true, true, (bool?)true);",
            "var (choice, dontRemind) = await PromptPreviewEditorLongLineWrapAsync(filePath, maxLineLength);",
            "ViewModel.PreviewLongLineWarningIndex = choice == PreviewEditorWrapChoice.NoWrap ? 1 : 2;",
            "await ViewModel.PersistSettingsAsync();");

        // The preference round-trips through persisted settings.
        Assert.Contains("PreviewLongLineWarningIndex = Math.Clamp(_settings.PreviewLongLineWarningIndex, 0, 2);", MainViewModelSource);
        Assert.Contains("_settings.PreviewLongLineWarningIndex = PreviewLongLineWarningIndex;", MainViewModelSource);
    }

    [Fact]
    public void EditorWrapToolbar_AppliesCurrentEditorBeforePreviewRefresh()
    {
        string wrapCommand = ExtractMethodWindow(MainWindowSource, "OnWrapModeOptionClicked", window: 1400);

        AssertContainsInOrder(wrapCommand,
            "ViewModel.PreviewWrapModeIndex = mode;",
            "bool wrap = mode == (int)Models.PreviewWrapMode.Wrap;",
            "ViewModel.PreviewWordWrap = wrap;",
            "SyncWrapModeToggles(mode);",
            "_previewEditorWrapOverride = null;",
            "ApplyPreviewEditorWordWrap(wrap);",
            "RefreshCurrentPreview(preserveScroll: true);");
    }

    [Fact]
    public void PreviewEditorSave_VerifiesDiskWriteAndShowsTimedOverlay()
    {
        string save = ExtractMethodWindow(PreviewEditorSource, "SavePreviewEditAsync", window: 4200);
        AssertContainsInOrder(save,
            "var textToSave = GetPreviewEditorText();",
            "await SavePreviewEditorTextToDiskAsync(textToSave).ConfigureAwait(true);",
            "await VerifyPreviewEditorSavedTextAsync(textToSave).ConfigureAwait(true);",
            "_previewEditorOriginalText = _previewEditorChunked ? null : textToSave;",
            "bool fileStillHasMatches = ViewModel.RevalidateFileResults(_previewEditorPath, textToSave);",
            "ViewModel.StatusText = $\"Saved {_previewEditorPath}.\";",
            "ShowPreviewEditorSavedOverlay();",
            "return true;");
        Assert.DoesNotContain("ViewModel.RevalidateFileResults(_previewEditorPath, GetPreviewEditorText())", save);

        string verify = ExtractMethodWindow(PreviewEditorSource, "VerifyPreviewEditorSavedTextAsync", window: 1200);
        Assert.Contains("File.ReadAllTextAsync(_previewEditorPath, _previewEditorEncoding)", verify);
        Assert.Contains("Saved file verification failed", verify);

        string write = ExtractMethodWindow(PreviewEditorSource, "SavePreviewEditorTextToDiskAsync", window: 3200);
        AssertContainsInOrder(write,
            "File.Move(tempPath, _previewEditorPath, overwrite: true);",
            "long totalByteLength = new FileInfo(_previewEditorPath).Length;",
            "await ApplyPreviewEditorChunkSaveStateAsync(encodedLoadedBytes, totalByteLength).ConfigureAwait(false);");
        Assert.DoesNotContain("_previewEditorLoadedByteLength = encodedLoadedBytes;\r\n            _previewEditorTotalByteLength", write);

        string chunkSaveState = ExtractMethodWindow(PreviewEditorSource, "ApplyPreviewEditorChunkSaveStateAsync", window: 2200);
        AssertContainsInOrder(chunkSaveState,
            "DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal",
            "_previewEditorLoadedByteLength = loadedByteLength;",
            "_previewEditorTotalByteLength = totalByteLength;",
            "UpdatePreviewEditorChunkUi();",
            "completion.TrySetResult(null);");

        string overlay = ExtractXamlWindow("x:Name=\"PreviewEditorSavedOverlay\"", 1200);
        Assert.Contains("Visibility=\"Collapsed\"", overlay);
        Assert.Contains("IsHitTestVisible=\"False\"", overlay);
        Assert.Contains("Glyph=\"&#xE8FB;\"", overlay);
        Assert.Contains("Text=\"Saved\"", overlay);

        Assert.Contains("private const int PreviewEditorSavedOverlayDurationMs = 700;", PreviewEditorSource);
        string showOverlay = ExtractMethodWindow(PreviewEditorSource, "ShowPreviewEditorSavedOverlay", window: 2200);
        AssertContainsInOrder(showOverlay,
            "if (!ViewModel.ShowEditorSavedOverlay || PreviewEditor.Visibility != Visibility.Visible)",
            "PreviewEditorSavedOverlay.Visibility = Visibility.Visible;",
            "timer.Interval = TimeSpan.FromMilliseconds(PreviewEditorSavedOverlayDurationMs);",
            "PreviewEditorSavedOverlay.Visibility = Visibility.Collapsed;");
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

    [Fact]
    public void TextControlBox_ManyLongLinesUseMultiLineHorizontalVirtualization()
    {
        // A file with MANY very long lines (e.g. a JSONL chat log) is NOT the single-giant-line case the
        // wrapped virtualization handles. In NoWrap the editor must slice EVERY visible line to the
        // horizontal window instead of laying out the multi-megabyte join of all visible lines every frame
        // (that per-frame O(total-visible-chars) layout is the scroll-stutter cause).
        string textRenderer = ReadTextControlBoxSource("Core", "Renderer", "TextRenderer.cs");
        string selectionRenderer = ReadTextControlBoxSource("Core", "Renderer", "SelectionRenderer.cs");

        // The slice window + per-line prefix mapping machinery exists.
        Assert.Contains("private bool ShouldHorizontallySlice(", textRenderer);
        Assert.Contains("private string BuildHorizontallySlicedText(", textRenderer);
        Assert.Contains("public int GetRenderedLayoutIndexForDocument(", textRenderer);
        Assert.Contains("_renderedLineSlicePrefix", textRenderer);
        Assert.Contains("public int HorizontalSliceLength", textRenderer);

        // Only pathological files (a very long visible line) take the slice path; a file without any line
        // over the threshold keeps the normal, untouched render path (and its well-tested caret/selection).
        string shouldSlice = ExtractMethodWindow(textRenderer, "ShouldHorizontallySlice", window: 2000);
        AssertContainsInOrder(shouldSlice,
            "maxLen <= HorizontalVirtualizationThreshold",
            "return false;");

        // Draw builds ONLY the sliced text for the multi-line long case (never the full join).
        string draw = ExtractMethodWindow(textRenderer, "Draw", window: 18000);
        AssertContainsInOrder(draw,
            "ShouldHorizontallySlice(canvasText, out hSliceStart, out hSliceLen)",
            "RenderedText = BuildHorizontallySlicedText(hSliceStart, hSliceLen);",
            "IsHorizontallyVirtualized = true;");

        // Selection maps both endpoints through the per-line prefix offsets, not cumulative full-line
        // lengths (which would over-count because the rendered prior lines are sliced).
        string selection = ExtractMethodWindow(selectionRenderer, "DrawSelection", window: 9000);
        AssertContainsInOrder(selection,
            "else if (textRenderer.IsHorizontallyVirtualized)",
            "textRenderer.GetRenderedLayoutIndexForDocument(startLine, characterPosStart)",
            "textRenderer.GetRenderedLayoutIndexForDocument(endLine, characterPosEnd)");

        // The current-line (caret / hit-test) layout is sliced at the SAME origin (HorizontalSliceStart) so
        // the caret's rendered index and pixel offset line up.
        string currentLine = ExtractMethodWindow(textRenderer, "UpdateCurrentLineTextLayout", window: 3000);
        AssertContainsInOrder(currentLine,
            "IsHorizontallyVirtualized && !IsWordWrapEnabled && HorizontalSliceLength > 0",
            "Math.Min(HorizontalSliceStart, lineText.Length)");
    }

    [Fact]
    public void PopOutEditor_TransfersStateToIndependentWindowAndRestoresPreview()
    {
        // The main editor toolbar exposes a pop-out button wired to OnPopOutPreviewEditor.
        string popOutButton = ExtractXamlWindow("x:Name=\"EditorPopOutButton\"", 320);
        Assert.Contains("Click=\"OnPopOutPreviewEditor\"", popOutButton);

        string handler = ExtractMethodWindow(PreviewEditorSource, "OnPopOutPreviewEditor", window: 4000);
        // Pop-out is gated by the configurable size limit; over-limit files show a message. A
        // fully-loaded file TRANSFERS its current state; a chunked (partial-load) file is RELOADED in
        // full so saving can't truncate it. Afterwards the in-pane editor closes and the read-only
        // preview surface (and its match navigation) is restored.
        AssertContainsInOrder(handler,
            "long limitBytes = PreviewEditorPopOutMaxByteLength;",
            "if (fileSize > limitBytes)",
            "pop-out limit",
            "if (_previewEditorChunked)",
            "context = await BuildReloadedPreviewEditorWindowContextAsync(_previewEditorPath, limitBytes);",
            "context = BuildPreviewEditorWindowContext();",
            "PreviewEditorWindow.Open(context);",
            "ClosePreviewEditor();",
            "RestorePreviewSurfaceAfterEditor();");

        // The limit is driven by the configurable setting (default 100 MB).
        Assert.Contains("PreviewEditorPopOutMaxByteLength => (long)Math.Max(1, ViewModel.PreviewEditorPopOutMaxSizeMB) * 1024 * 1024", PreviewEditorSource);
        Assert.Contains("public int PreviewEditorPopOutMaxSizeMB { get; set; } = 100;", File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs")));

        // The transferred state carries the CURRENT (possibly-edited) text plus the on-disk baseline
        // so the pop-out window opens already-dirty when there were unsaved edits.
        string context = ExtractMethodWindow(PreviewEditorSource, "BuildPreviewEditorWindowContext", window: 3200);
        AssertContainsInOrder(context,
            "string currentText = GetPreviewEditorText();",
            "string diskText = _previewEditorOriginalText ?? currentText;",
            "Text = currentText,",
            "DiskText = diskText,",
            "Encoding = _previewEditorEncoding,",
            "WordWrap = PreviewEditor.WordWrap,",
            "ZoomFactor = PreviewEditor.ZoomFactor,",
            "BackupBeforeSave = ViewModel.BackupBeforeSave,");
    }

    [Fact]
    public void PopOutEditorWindow_IsIndependentOfMainWindowMatchPaginator()
    {
        // The pop-out window is a self-contained Window that owns its own editor and save path.
        Assert.Contains("internal sealed class PreviewEditorWindow : Window", PreviewEditorWindowSource);
        Assert.Contains("private readonly TextControlBoxNS.TextControlBox _editor;", PreviewEditorWindowSource);

        // Yagu's global match paginator assumes all previewed docs live on the single main-window
        // screen. The popped-out editor must therefore NOT touch any match-navigation state, or a
        // second screen's edits would corrupt the main window's paginator.
        Assert.DoesNotContain("_matchParagraphs", PreviewEditorWindowSource);
        Assert.DoesNotContain("_currentMatchIndex", PreviewEditorWindowSource);
        Assert.DoesNotContain("MatchNavPanel", PreviewEditorWindowSource);
        Assert.DoesNotContain("ActiveMatchOverlay", PreviewEditorWindowSource);
        Assert.DoesNotContain("SectionNav", PreviewEditorWindowSource);

        // It saves straight to disk with the transferred encoding (honoring backup + UTF-8 fallback).
        string save = ExtractMethodWindow(PreviewEditorWindowSource, "SaveAsync", window: 2800);
        AssertContainsInOrder(save,
            "if (_context.BackupBeforeSave && File.Exists(_context.FilePath))",
            "EditorEncodingHelper.HasUnencodableCharacters(textToSave, _encoding)",
            "await File.WriteAllTextAsync(_context.FilePath, textToSave, _encoding)",
            "_dirty = false;",
            "ShowSavedOverlay();");

        // Closing with unsaved edits is cancellable and prompts Save/Discard/Cancel before it lets
        // the OS destroy the window.
        string closing = ExtractMethodWindow(PreviewEditorWindowSource, "OnAppWindowClosing", window: 1800);
        AssertContainsInOrder(closing,
            "if (_forceClose || !HasRealChanges())",
            "args.Cancel = true;",
            "Unsaved changes",
            "_forceClose = true;",
            "Close();");
    }

    [Fact]
    public void PopOutEditorWindow_IsIndependentTopLevelNotOwned()
    {
        // A popped-out editor is an INDEPENDENT top-level window (not GWL_HWNDPARENT-owned to the main
        // window), so it survives the main window minimizing / hiding to tray and can live on a second
        // monitor. An owned window is hidden by Windows whenever its owner hides.
        Assert.DoesNotContain("ConfigureOwnedWindow(_hwnd", PreviewEditorWindowSource);
        Assert.Contains("SetForegroundWindow", PreviewEditorWindowSource);

        // The main window keeps itself visible (does not hide to tray) while a pop-out is open.
        Assert.Contains("public static bool HasOpenOwnedWindow(IntPtr ownerHwnd)", PreviewEditorWindowSource);
        Assert.Contains("PreviewEditorWindow.HasOpenOwnedWindow(_hwnd)", MainWindowSource);
    }

    [Fact]
    public void PopOutEditorWindow_ThemesCaptionSolid_AndAutoArranges()
    {
        // White-bar fix: the standard (non-extended) title bar paints the caption buttons with a SOLID
        // background (matching the title bar), NOT Transparent — a transparent caption strip over
        // nothing renders as the system-default white bar.
        string theme = ExtractMethodWindow(PreviewEditorWindowSource, "ThemeStandardTitleBar", window: 2200);
        Assert.Contains("titleBar.ButtonBackgroundColor = background;", theme);
        Assert.DoesNotContain("titleBar.ButtonBackgroundColor = Colors.Transparent", theme);

        // Auto-tiling: opening or closing a pop-out reflows every sibling window into the owner
        // monitor's work area via the pure PopOutTileLayout geometry.
        Assert.Contains("ArrangeOpenWindows(context.OwnerHwnd, context.Arrangement);", PreviewEditorWindowSource);
        string arrange = ExtractMethodWindow(PreviewEditorWindowSource, "ArrangeOpenWindows", window: 2200);
        AssertContainsInOrder(arrange,
            "if (!PopOutTileLayout.IsTiling(mode))",
            "WindowForegroundHelper.TryGetWorkArea(ownerHwnd",
            "PopOutTileLayout.Compute(windows.Count, mode",
            "MoveAndResize(new Windows.Graphics.RectInt32");
        string closed = ExtractMethodWindow(PreviewEditorWindowSource, "OnClosed", window: 900);
        Assert.Contains("ArrangeOpenWindows(_ownerHwnd, _arrangement);", closed);

        // The chosen arrangement flows from the setting through ResolvePopOutArrangement into every
        // pop-out context (editor transfer, chunked reload, and drawer).
        Assert.Contains("Yagu.Helpers.PopOutTileLayout.FromIndex(ViewModel.PreviewEditorPopOutArrangementIndex)", PreviewEditorSource);
        int arrangementFields = System.Text.RegularExpressions.Regex.Matches(PreviewEditorSource, "Arrangement = ResolvePopOutArrangement\\(\\),").Count;
        Assert.Equal(3, arrangementFields);
    }

    [Fact]
    public void PopOutDrawer_OpensReadOnlyPreviewThatUnlocksToEditInSameWindow()
    {
        // Each preview drawer header has a pop-out button wired to the drawer pop-out handler.
        string header = ExtractMethodWindow(MainWindowSource, "BuildPreviewSectionHeader", 9000);
        Assert.Contains("await PopOutPreviewDrawerAsync(path, popOutResults);", header);

        // The drawer pop-out loads the whole file read-only (reusing the shared static loader), jumps
        // to the first real match line, and highlights the literal search term.
        string popout = ExtractMethodWindow(PreviewEditorSource, "PopOutPreviewDrawerAsync", window: 3800);
        AssertContainsInOrder(popout,
            "document = await LoadPreviewDocumentAsync(",
            "if (result.LineNumber > 0) { scrollToLine = result.LineNumber; break; }",
            "StartReadOnly = true,",
            "ScrollToLine = scrollToLine,",
            "HighlightWord = literalHighlight ? ViewModel.LastSearchPattern : null,",
            "PreviewEditorWindow.Open(context);");

        // The window honors read-only mode and offers an "Edit file" button that unlocks editing in
        // the SAME window (the workflow: pop out the drawer, then click Edit file).
        Assert.Contains("public bool StartReadOnly { get; init; }", PreviewEditorWindowSource);
        Assert.Contains("Text = \"Edit file\"", PreviewEditorWindowSource);

        string enterEdit = ExtractMethodWindow(PreviewEditorWindowSource, "EnterEditMode", window: 1000);
        AssertContainsInOrder(enterEdit,
            "_readOnly = false;",
            "_editor.IsReadOnly = false;",
            "UpdateModeUi();");

        string loaded = ExtractMethodWindow(PreviewEditorWindowSource, "OnEditorLoaded", window: 2800);
        AssertContainsInOrder(loaded,
            "_editor.IsReadOnly = _readOnly;",
            "_editor.BeginSearch(_context.HighlightWord",
            "_editor.ScrollLineToCenter(_context.ScrollToLine - 1)");
    }

    private static SearchResult CreateResult(string filePath, int lineNumber) =>
        new(filePath, lineNumber, $"line {lineNumber} test", 5, 4, Array.Empty<string>(), Array.Empty<string>());

    private static string ReadMainWindowSources()
    {
        string yaguRoot = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow");
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
            Path.Combine(RepoRoot, "src", "vendor", "TextControlBox-WinUI", "TextControlBox"),
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