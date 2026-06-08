namespace Yagu.Tests;

/// <summary>
/// Source-verification tests for the settings-window migration and
/// SelectionRenderer bounds-check fix.  These pin the source-level
/// contracts so that regressions in untestable WinUI/Win2D code are
/// caught without requiring the WindowsAppSDK runtime.
/// </summary>
public sealed class SettingsWindowRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs"));
    private static readonly string MainWindowLauncherSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs"));
    private static readonly string MainWindowStartupChecksSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
    private static readonly string MainWindowTerminalSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string SettingsWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml"));
    private static readonly string SettingsServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
    private static readonly string ConPtyTerminalServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Services", "ConPtyTerminalService.cs"));
    private static readonly string AppSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "App.xaml.cs"));
    private static readonly string HelpWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Help", "HelpWindow.xaml.cs"));
    private static readonly string ResultStoreTempLocationWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "ResultStoreTempLocationWindow.cs"));
    private static readonly string WindowForegroundHelperSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Helpers", "WindowForegroundHelper.cs"));
    private static readonly string SelectionRendererSource = File.ReadAllText(
        Path.Combine(RepoRoot, "vendor", "TextControlBox-WinUI", "TextControlBox",
            "Core", "Renderer", "SelectionRenderer.cs"));

    // ── SettingsWindow XAML structure ──

    [Fact]
    public void SettingsWindowXaml_HasMicaBackdrop()
    {
        Assert.Contains("<MicaBackdrop />", SettingsWindowXaml);
    }

    [Fact]
    public void SettingsWindowXaml_HasCustomTitleBar()
    {
        Assert.Contains("x:Name=\"AppTitleBar\"", SettingsWindowXaml);
        Assert.Contains("Settings", SettingsWindowXaml);
    }

    [Fact]
    public void SettingsWindowXaml_HasVerticalTabLayout()
    {
        Assert.Contains("x:Name=\"TabList\"", SettingsWindowXaml);
        Assert.Contains("<ListView", SettingsWindowXaml);
        Assert.Contains("SelectionChanged=\"OnTabSelectionChanged\"", SettingsWindowXaml);
    }

    [Fact]
    public void SettingsWindowXaml_HasScrollableContent()
    {
        Assert.Contains("<ScrollViewer", SettingsWindowXaml);
        Assert.Contains("x:Name=\"SettingsContent\"", SettingsWindowXaml);
    }

    [Fact]
    public void SettingsWindowXaml_HasSaveAndCancelButtons()
    {
        Assert.Contains("Click=\"OnSaveClick\"", SettingsWindowXaml);
        Assert.Contains("Click=\"OnCancelClick\"", SettingsWindowXaml);
        Assert.Contains("Style=\"{ThemeResource AccentButtonStyle}\"", SettingsWindowXaml);
        Assert.Contains("IsEnabled=\"False\"", SettingsWindowXaml);
    }

    // ── SettingsWindow code-behind structure ──

    [Fact]
    public void SettingsWindow_AcceptsRequiredDependencies()
    {
        // Constructor takes ViewModel, HotkeyService, mainHwnd, ApplyWordWrap callback
        Assert.Contains("MainViewModel viewModel", SettingsWindowSource);
        Assert.Contains("HotkeyService hotkeyService", SettingsWindowSource);
        Assert.Contains("IntPtr mainHwnd", SettingsWindowSource);
        Assert.Contains("Action<bool>? applyWordWrap", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_CentersOverOwnerWindow()
    {
        Assert.Contains("CenterOverOwner(", SettingsWindowSource);
        Assert.Contains("GetWindowRect(", SettingsWindowSource);
        // Verify centering arithmetic
        Assert.Contains("ownerCx - w / 2", SettingsWindowSource);
        Assert.Contains("ownerCy - h / 2", SettingsWindowSource);
        // Clamped to monitor work area so window stays on-screen
        Assert.Contains("MonitorFromWindow(", SettingsWindowSource);
        Assert.Contains("GetMonitorInfo(", SettingsWindowSource);
        Assert.Contains("rcWork", SettingsWindowSource);
    }

    [Fact]
    public void AppOwnedWindows_AreOwnedAndRaisedAboveMainWindow()
    {
        Assert.Contains("GwlpHwndParent = -8", WindowForegroundHelperSource);
        Assert.Contains("SetWindowLongPtr(childHwnd, GwlpHwndParent, ownerHwnd)", WindowForegroundHelperSource);
        Assert.Contains("IsTopMost(ownerHwnd)", WindowForegroundHelperSource);
        Assert.Contains("SetWindowPos(childHwnd, HwndTopmost", WindowForegroundHelperSource);
        Assert.Contains("SetForegroundWindow(childHwnd)", WindowForegroundHelperSource);

        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);", SettingsWindowSource);
        Assert.Contains("public void BringInFrontOfMainWindow()", SettingsWindowSource);
        string settingsHelper = ExtractMethod(MainWindowSource, "OpenSettingsTab", window: 2200);
        Assert.Contains("_settingsWindow.BringInFrontOfMainWindow();", settingsHelper);

        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);", HelpWindowSource);
        Assert.Contains("public void BringInFrontOfMainWindow(IntPtr mainHwnd)", HelpWindowSource);
        string helpHelper = ExtractMethod(MainWindowSource, "OpenHelpWindow", window: 1800);
        Assert.Contains("_helpWindow.BringInFrontOfMainWindow(_hwnd);", helpHelper);

        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);", ResultStoreTempLocationWindowSource);
        Assert.Contains("WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);", ResultStoreTempLocationWindowSource);
    }

    [Fact]
    public void SettingsWindow_SetsUpCustomTitleBar()
    {
        Assert.Contains("ExtendsContentIntoTitleBar = true;", SettingsWindowSource);
        Assert.Contains("SetTitleBar(AppTitleBar);", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_SavePersistsAndCloses()
    {
        string saveMethod = ExtractMethod(SettingsWindowSource, "OnSaveClick");
        Assert.Contains("SaveButton.IsEnabled = false;", saveMethod);
        Assert.Contains("await _viewModel.PersistSettingsAsync();", saveMethod);
        Assert.Contains("MarkSettingsClean();", saveMethod);
        Assert.Contains("Close()", saveMethod);
    }

    [Fact]
    public void SettingsWindow_SaveButtonTracksDirtySettingChanges()
    {
        Assert.Contains("private bool _settingsDirty;", SettingsWindowSource);
        Assert.Contains("private bool _settingDirtyTrackingEnabled;", SettingsWindowSource);
        Assert.Contains("private readonly Dictionary<UIElement, object?> _cleanSettingValues = new();", SettingsWindowSource);
        Assert.Contains("x:Name=\"SettingsContentScrollViewer\"", SettingsWindowXaml);
        Assert.Contains("Background=\"Transparent\"", SettingsWindowXaml);
        Assert.Contains("PointerPressed=\"OnSettingsContentPointerPressed\"", SettingsWindowXaml);
        AssertContainsInOrder(SettingsWindowSource,
            "BuildSettingsContent();",
            "AttachSettingDirtyHandlers();",
            "MarkSettingsClean();",
            "ExtractSearchableEntries();");

        string attachMethod = ExtractMethod(SettingsWindowSource, "AttachSettingDirtyHandlers", window: 3000);
        Assert.Contains("textBox.TextChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("numberBox.ValueChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("comboBox.SelectionChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("checkBox.Checked += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("toggleSwitch.Toggled += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("calendarDatePicker.DateChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("colorPicker.ColorChanged += (_, _) => MarkSettingsDirty();", attachMethod);

        string dirtyMethod = ExtractMethod(SettingsWindowSource, "MarkSettingsDirty", window: 900);
        AssertContainsInOrder(dirtyMethod,
            "if (!_settingDirtyTrackingEnabled || _settingsDirty)",
            "_settingsDirty = true;",
            "SaveButton.IsEnabled = true;");

        string cleanMethod = ExtractMethod(SettingsWindowSource, "MarkSettingsClean", window: 500);
        AssertContainsInOrder(cleanMethod,
            "_settingsDirty = false;",
            "SaveButton.IsEnabled = false;",
            "CaptureCleanSettingValues();");

        string pointerMethod = ExtractMethod(SettingsWindowSource, "OnSettingsContentPointerPressed", window: 1200);
        AssertContainsInOrder(pointerMethod,
            "IsInsideSettingEditor(source)",
            "SettingsContentScrollViewer.Focus(FocusState.Pointer);",
            "MarkSettingsDirtyIfCurrentValuesChanged");

        string valueCheckMethod = ExtractMethod(SettingsWindowSource, "MarkSettingsDirtyIfCurrentValuesChanged", window: 1400);
        AssertContainsInOrder(valueCheckMethod,
            "if (!_settingDirtyTrackingEnabled || _settingsDirty)",
            "foreach (var element in _dirtyTrackedElements)",
            "TryGetSettingValue(element, out var currentValue)",
            "!Equals(cleanValue, currentValue)",
            "MarkSettingsDirty();");

        string settingValueMethod = ExtractMethod(SettingsWindowSource, "TryGetSettingValue", window: 1600);
        Assert.Contains("value = (numberBox.Value, numberBox.Text ?? string.Empty);", settingValueMethod);

        AssertContainsInOrder(SettingsWindowSource,
            "_viewModel.SuppressAdminWarning = false;",
            "MarkSettingsDirty();",
            "resetAdmin.IsEnabled = false;");
    }

    [Fact]
    public void SettingsWindow_CancelCloses()
    {
        string cancelMethod = ExtractMethod(SettingsWindowSource, "OnCancelClick");
        Assert.Contains("Close()", cancelMethod);
    }

    [Theory]
    [InlineData("Search Defaults")]
    [InlineData("Search Limits")]
    [InlineData("Performance")]
    [InlineData("Display")]
    [InlineData("Editor")]
    [InlineData("Window")]
    [InlineData("General")]
    public void SettingsWindow_ContainsAllSettingsGroups(string groupName)
    {
        Assert.Contains($"AddTab(\"{groupName}\")", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_TabSelectionSwapsContent()
    {
        string method = ExtractMethod(SettingsWindowSource, "OnTabSelectionChanged");
        Assert.Contains("SettingsContent.Children.Clear()", method);
        Assert.Contains("_tabPages", method);
    }

    [Fact]
    public void SettingsWindow_WordWrapToggleCallsApplyWordWrap()
    {
        Assert.Contains("_applyWordWrap?.Invoke(true)", SettingsWindowSource);
        Assert.Contains("_applyWordWrap?.Invoke(false)", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_FontSelectorsUseSystemFontPreviewPicker()
    {
        Assert.Contains("CanvasTextFormat.GetSystemFontFamilies()", SettingsWindowSource);
        Assert.Contains("CreateFontFamilyPicker(", SettingsWindowSource);
        Assert.Contains("new XamlFontFamily(fontFamily)", SettingsWindowSource);
        Assert.DoesNotContain("var resultMatchFontFamily = new TextBox", SettingsWindowSource);
        Assert.DoesNotContain("var editorFontFamily = new TextBox", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_DisplayAppearanceSettingsAreGroupedBySurface()
    {
        Assert.Contains("AddSettingsGroupBox", SettingsWindowSource);
        AssertContainsInOrder(SettingsWindowSource,
            "var g = AddTab(\"Display\");",
            "var fileMatchListGroup = AddSettingsGroupBox(g, \"File Match List\");",
            "var previewViewerGroup = AddSettingsGroupBox(g, \"Preview Viewer\");",
            "var editorAppearanceGroup = AddSettingsGroupBox(g, \"Editor\");");

        int displayStart = SettingsWindowSource.IndexOf("// ── Display ──", StringComparison.Ordinal);
        int editorTabStart = SettingsWindowSource.IndexOf("// ── Editor ──", StringComparison.Ordinal);
        int windowTabStart = SettingsWindowSource.IndexOf("// ── Window ──", StringComparison.Ordinal);
        Assert.True(displayStart >= 0 && editorTabStart > displayStart && windowTabStart > editorTabStart);

        string displayBlock = SettingsWindowSource[displayStart..editorTabStart];
        string editorTabBlock = SettingsWindowSource[editorTabStart..windowTabStart];

        Assert.Contains("fileMatchListGroup.Children.Add", displayBlock);
        Assert.Contains("previewViewerGroup.Children.Add", displayBlock);
        Assert.Contains("editorAppearanceGroup.Children.Add", displayBlock);
        Assert.Contains("Results list match text", displayBlock);
        Assert.Contains("Preview font colors", displayBlock);
        Assert.Contains("_viewModel.PreviewEditorFontFamily", displayBlock);
        Assert.Contains("Editor gutter text:", displayBlock);

        Assert.Contains("CaptureTabPageRootElements();", SettingsWindowSource);
        Assert.Contains("OriginalPlacements = capturedPlacements", SettingsWindowSource);
        Assert.Contains("RestoreOriginalPlacements(entry.OriginalPlacements)", SettingsWindowSource);
        AssertContainsInOrder(SettingsWindowSource,
            "if (child is Border { Child: UIElement groupChild })",
            "EnumerateSearchableGroupChild(groupChild)");

        Assert.DoesNotContain("_viewModel.PreviewEditorFontFamily", editorTabBlock);
        Assert.DoesNotContain("Editor gutter text:", editorTabBlock);
    }

    [Fact]
    public void SettingsWindow_HotkeySection_QueriesAvailableKeys()
    {
        Assert.Contains("GetAvailableCtrlShiftLetterKeys(_mainHwnd)", SettingsWindowSource);
        Assert.Contains("ChooseAvailableKey(", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_TerminalEmulatorSectionConfiguresDefaultWorkingDirectory()
    {
        Assert.Contains("\"Terminal Emulator\"", SettingsWindowSource);
        Assert.Contains("Default working directory:", SettingsWindowSource);
        Assert.Contains("_viewModel.TerminalDefaultWorkingDirectory", SettingsWindowSource);
        Assert.Contains("Width = 620", SettingsWindowSource);
        Assert.Contains("PickTerminalWorkingDirectoryAsync", SettingsWindowSource);
        Assert.Contains("App.LaunchWorkingDirectory", SettingsWindowSource);

        AssertContainsInOrder(SettingsWindowSource,
            "var g = AddTab(\"UI Behaviors\");",
            "var g = AddTab(\"Terminal Emulator\");",
            "AddTerminalEmulationSetting(g);",
            "var g = AddTab(\"Developer Options\");");

        int uiBehaviorStart = SettingsWindowSource.IndexOf("var g = AddTab(\"UI Behaviors\");", StringComparison.Ordinal);
        int terminalEmulatorStart = SettingsWindowSource.IndexOf("var g = AddTab(\"Terminal Emulator\");", StringComparison.Ordinal);
        Assert.True(uiBehaviorStart >= 0 && terminalEmulatorStart > uiBehaviorStart);
        string uiBehaviorBlock = SettingsWindowSource[uiBehaviorStart..terminalEmulatorStart];
        Assert.DoesNotContain("AddTerminalEmulationSetting", uiBehaviorBlock);
    }

    [Fact]
    public void TerminalDefaultWorkingDirectory_PersistsAndStartsConPtyInResolvedDirectory()
    {
        Assert.Contains("public string TerminalDefaultWorkingDirectory", SettingsServiceSource);
        Assert.Contains("TerminalDefaultWorkingDirectory = _settings.TerminalDefaultWorkingDirectory", MainViewModelSource);
        Assert.Contains("_settings.TerminalDefaultWorkingDirectory =", MainViewModelSource);
        Assert.Contains("[ObservableProperty] public partial string TerminalDefaultWorkingDirectory", MainViewModelSource);
        Assert.Contains("public static string LaunchWorkingDirectory", AppSource);
        Assert.Contains("workingDirectory: ResolveTerminalWorkingDirectory()", MainWindowTerminalSource);
        Assert.Contains("TryResolveExistingDirectory(ViewModel.TerminalDefaultWorkingDirectory", MainWindowTerminalSource);
        Assert.Contains("TryResolveExistingDirectory(App.LaunchWorkingDirectory", MainWindowTerminalSource);
        Assert.Contains("Start(int cols = 120, int rows = 30, string? workingDirectory = null)", ConPtyTerminalServiceSource);
        Assert.Contains("SpawnProcess(\"pwsh.exe\", workingDirectory)", ConPtyTerminalServiceSource);
        Assert.Contains("workingDirectory, ref startupInfo", ConPtyTerminalServiceSource);
    }

    // ── MainWindow settings integration ──

    [Fact]
    public void MainWindow_OnOpenSettings_CreatesSettingsWindow()
    {
        string method = ExtractMethod(MainWindowSource, "OnOpenSettings", window: 800);
        Assert.Contains("OpenSettingsTab();", method);
        // Settings are now in a standalone window, not a ContentDialog
        Assert.DoesNotContain("new ContentDialog", method);

        string helper = ExtractMethod(MainWindowSource, "OpenSettingsTab", window: 1800);
        Assert.Contains("new SettingsWindow(", helper);
        Assert.Contains("_settingsWindow.Activate()", helper);
    }

    [Fact]
    public void MainWindow_OnOpenSettings_ReusesExistingWindow()
    {
        string method = ExtractMethod(MainWindowSource, "OpenSettingsTab");
        Assert.Contains("if (_settingsWindow is not null)", method);
        // try-catch around Activate to handle the case where the window was already closed
        Assert.Contains("_settingsWindow.Activate();", method);
        Assert.Contains("_settingsWindow.SelectTab(tabIndex.Value);", method);
        Assert.Contains("catch { _settingsWindow = null; }", method);
    }

    [Fact]
    public void MainWindow_OnOpenSettings_ClearsReferenceOnClose()
    {
        string method = ExtractMethod(MainWindowSource, "OpenSettingsTab");
        Assert.Contains("_settingsWindow.Closed += ", method);
        Assert.Contains("_settingsWindow = null", method);
    }

    [Fact]
    public void SettingsWindow_SelectTab_RestoresNormalTabViewBeforeSelecting()
    {
        string method = ExtractMethod(SettingsWindowSource, "SelectTab", window: 1800);
        Assert.Contains("_isSearchActive", method);
        Assert.Contains("SearchBox.Text = string.Empty;", method);
        Assert.Contains("TabList.Visibility = Visibility.Visible;", method);
        Assert.Contains("ClearSearchResultContainers();", method);
        Assert.Contains("RestoreTabPageElements();", method);
        AssertContainsInOrder(method,
            "TabList.SelectedIndex = index;",
            "SettingsContent.Children.Clear();",
            "SettingsContent.Children.Add(_tabPages[index]);");
    }

    [Fact]
    public void MainWindow_SettingsHelperCanOpenPerformanceAndDisplayTabs()
    {
        Assert.Contains("private const int SettingsPerformanceTabIndex = 2;", MainWindowSource);
        Assert.Contains("private const int SettingsDisplayTabIndex = 3;", MainWindowSource);
        Assert.Contains("OpenSettingsTab(SettingsPerformanceTabIndex);", MainWindowSource);
    }

    [Fact]
    public void MainWindow_OnWindowActivated_SuppressesTrayHideForSettings()
    {
        string method = ExtractMethod(MainWindowLauncherSource, "OnWindowActivated");
        AssertContainsInOrder(method,
            "WindowActivationState.Deactivated",
            "HasOpenAppOwnedWindowOrModal()",
            "return;",
            "HideToTray()");

        string helper = ExtractMethod(MainWindowLauncherSource, "HasOpenAppOwnedWindowOrModal", window: 800);
        Assert.Contains("_settingsWindow is not null", helper);
        Assert.Contains("_helpWindow is not null", helper);
        Assert.Contains("_ownedModalWindowDepth > 0", helper);

        Assert.Contains("_ownedModalWindowDepth++;", MainWindowStartupChecksSource);
        Assert.Contains("_ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);", MainWindowStartupChecksSource);
    }

    [Fact]
    public void MainWindow_HasSettingsWindowField()
    {
        Assert.Contains("private SettingsWindow? _settingsWindow;", MainWindowSource);
    }

    [Fact]
    public void MainWindowXaml_FilterFlyoutOpensBelowToolbarButton()
    {
        Assert.Contains("<MenuFlyout Placement=\"BottomEdgeAlignedLeft\" Opening=\"OnFilterFlyoutOpening\">", MainWindowXaml);
    }

    [Fact]
    public void MainWindow_NoLongerContainsOldSettingsMethods()
    {
        // The old helper methods that were moved to SettingsWindow should not be in MainWindow.
        Assert.DoesNotContain("private static StackPanel NextSearchLabel(", MainWindowSource);
        Assert.DoesNotContain("private static string SettingsGroupIcon(", MainWindowSource);
        Assert.DoesNotContain("private FrameworkElement BuildSettingsPanel(", MainWindowSource);
    }

    // ── SelectionRenderer bounds-check fix ──

    [Fact]
    public void SelectionRenderer_DrawSelection_ReturnsEarlyOnEmptyLines()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        AssertContainsInOrder(method,
            "totalLines.Count == 0",
            "return;");
    }

    [Fact]
    public void SelectionRenderer_DrawSelection_ClampsStartLineUpperBound()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        // startLine >= Count → clamp to Count-1
        Assert.Contains("startLine >= textManager.totalLines.Count", method);
        Assert.Contains("startLine = textManager.totalLines.Count - 1;", method);
    }

    [Fact]
    public void SelectionRenderer_DrawSelection_ClampsStartLineLowerBound()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        Assert.Contains("if (startLine < 0)", method);
        Assert.Contains("startLine = 0;", method);
    }

    [Fact]
    public void SelectionRenderer_DrawSelection_ClampsEndLineUpperBound()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        // >= instead of old >
        Assert.Contains("endLine >= textManager.totalLines.Count", method);
        Assert.Contains("endLine = textManager.totalLines.Count - 1;", method);
    }

    [Fact]
    public void SelectionRenderer_DrawSelection_ClampsEndLineLowerBound()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        Assert.Contains("if (endLine < 0)", method);
        Assert.Contains("endLine = 0;", method);
    }

    [Fact]
    public void SelectionRenderer_DrawSelection_ClampsLastRenderedLineToTotalLines()
    {
        string method = ExtractMethod(SelectionRendererSource, "DrawSelection");
        // lastRenderedLine must not exceed totalLines
        Assert.Contains("Math.Min(", method);
        Assert.Contains("textManager.totalLines.Count - 1)", method);
        // Guard for negative lastRenderedLine
        AssertContainsInOrder(method,
            "lastRenderedLine < 0",
            "return;");
    }

    // ── Helpers ──

    private static string ExtractMethod(string source, string methodName, int window = 4000)
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
            Assert.True(index >= 0, $"Method definition '{methodName}' not found.");

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
