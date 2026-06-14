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
    private static readonly string MainWindowWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));
    private static readonly string MainWindowKeyboardSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Keyboard.cs"));
    private static readonly string MainWindowIntroTipsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.IntroTips.cs"));
    private static readonly string MainWindowMatchNavSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.MatchNav.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string SettingsWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml"));
    private static readonly string SettingsServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
    private static readonly string AppThemeServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Services", "AppThemeService.cs"));
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
    private static readonly string AdminProtectedPathsDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "AdminProtectedPathsDialog.cs"));
    private static readonly string YaguDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "YaguDialog.cs"));
    private static readonly string WindowForegroundHelperSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Helpers", "WindowForegroundHelper.cs"));
    private static readonly string SelectionRendererSource = File.ReadAllText(
        Path.Combine(RepoRoot, "vendor", "TextControlBox-WinUI", "TextControlBox",
            "Core", "Renderer", "SelectionRenderer.cs"));
    private static readonly string LinkHighlightManagerSource = File.ReadAllText(
        Path.Combine(RepoRoot, "vendor", "TextControlBox-WinUI", "TextControlBox",
            "Core", "Text", "LinkHighlightManager.cs"));
    private static readonly string TerminalHtml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "Assets", "terminal.html"));

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
    public void SettingsWindowXaml_HasSaveAndCloseButtons()
    {
        Assert.Contains("Click=\"OnSaveClick\"", SettingsWindowXaml);
        Assert.Contains("Content=\"Close\"", SettingsWindowXaml);
        Assert.Contains("Click=\"OnCloseClick\"", SettingsWindowXaml);
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
        Assert.Contains("Action openHelp", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_CentersOverOwnerWindow()
    {
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, mainHwnd, w, h);", SettingsWindowSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, mainHwnd, windowWidth, windowHeight);", HelpWindowSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, width, height, minHeight: 300);", ResultStoreTempLocationWindowSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, DialogWidth, DialogHeight, minHeight: 300);", AdminProtectedPathsDialogSource);

        Assert.Contains("public static void CenterWindowOverOwner(", WindowForegroundHelperSource);
        Assert.Contains("GetWindowRect(", WindowForegroundHelperSource);
        // Verify centering arithmetic
        Assert.Contains("ownerCenterX - width / 2", WindowForegroundHelperSource);
        Assert.Contains("ownerCenterY - height / 2", WindowForegroundHelperSource);
        // Clamped to monitor work area so window stays on-screen
        Assert.Contains("MonitorFromWindow(", WindowForegroundHelperSource);
        Assert.Contains("GetMonitorInfo(", WindowForegroundHelperSource);
        Assert.Contains("rcWork", WindowForegroundHelperSource);
        Assert.Contains("appWindow.MoveAndResize(CalculateCenteredBounds(ownerHwnd, width, height, minWidth, minHeight));", WindowForegroundHelperSource);
    }

    [Fact]
    public void SettingsFontContrastWarning_CentersOverMainWindow()
    {
        string timerHandler = ExtractMethod(SettingsWindowSource, "OnFontContrastCheckTimerTick", window: 500);

        Assert.Contains("FontContrastWarningDialog.ShowIfNeededAsync(", timerHandler);
        Assert.Contains("_mainHwnd", timerHandler);
        Assert.DoesNotContain("WindowForegroundHelper.GetWindowHandle(this)", timerHandler);
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

        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(dialogHwnd, _ownerHwnd);", AdminProtectedPathsDialogSource);
        Assert.Contains("WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);", AdminProtectedPathsDialogSource);
    }

    [Fact]
    public void AdminProtectedPathsDialog_IsOwnedFixedSizeWindow()
    {
        string learnMore = ExtractMethod(MainWindowSource, "OnAdminLearnMore", window: 500);
        Assert.Contains("await AdminProtectedPathsDialog.ShowAsync(_hwnd, segments);", learnMore);
        Assert.DoesNotContain("new ContentDialog", learnMore);
        Assert.DoesNotContain("XamlRoot", learnMore);

        Assert.Contains("internal sealed class AdminProtectedPathsDialog : Window", AdminProtectedPathsDialogSource);
        Assert.Contains("private const int DialogWidth = 720;", AdminProtectedPathsDialogSource);
        Assert.Contains("private const int DialogHeight = 540;", AdminProtectedPathsDialogSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, DialogWidth, DialogHeight, minHeight: 300);", AdminProtectedPathsDialogSource);
        Assert.Contains("presenter.IsResizable = false;", AdminProtectedPathsDialogSource);
        Assert.Contains("EnableWindow(_ownerHwnd, false);", AdminProtectedPathsDialogSource);
        Assert.Contains("EnableWindow(_ownerHwnd, true);", AdminProtectedPathsDialogSource);
        Assert.Contains("ScrollViewer", AdminProtectedPathsDialogSource);
        Assert.Contains("FontFamily = new FontFamily(\"Consolas\")", AdminProtectedPathsDialogSource);
    }

    [Fact]
    public void SettingsWindow_SetsUpCustomTitleBar()
    {
        Assert.Contains("ExtendsContentIntoTitleBar = true;", SettingsWindowSource);
        Assert.Contains("SetTitleBar(AppTitleBar);", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_SavePersistsWithoutClosing()
    {
        string saveMethod = ExtractMethod(SettingsWindowSource, "OnSaveClick");
        Assert.Contains("SaveButton.IsEnabled = false;", saveMethod);
        Assert.Contains("await _viewModel.PersistSettingsAsync();", saveMethod);
        Assert.Contains("MarkSettingsClean();", saveMethod);
        Assert.DoesNotContain("Close()", saveMethod);
    }

    [Fact]
    public void EditorSavedOverlaySetting_IsPersistedAndShownInEditorSettings()
    {
        Assert.Contains("public bool ShowEditorSavedOverlay { get; set; } = true;", SettingsServiceSource);
        Assert.Contains("[ObservableProperty] public partial bool ShowEditorSavedOverlay { get; set; } = true;", MainViewModelSource);
        Assert.Contains("ShowEditorSavedOverlay = _settings.ShowEditorSavedOverlay;", MainViewModelSource);
        Assert.Contains("_settings.ShowEditorSavedOverlay = ShowEditorSavedOverlay;", MainViewModelSource);

        string editorSettings = ExtractMethod(SettingsWindowSource, "BuildSettingsContent", window: 90000);
        AssertContainsInOrder(editorSettings,
            "var saveSafetyGroup = AddSettingsGroupBox(g, \"Built-In Editor Saves\");",
            "Content = \"Show saved confirmation after saving\"",
            "_viewModel.ShowEditorSavedOverlay = true;",
            "_viewModel.ShowEditorSavedOverlay = false;");
    }

    [Fact]
    public void ThemeMode_IsPersistedAndAppliedFromUiSettings()
    {
        Assert.Contains("public int ThemeModeIndex { get; set; } // 0 = Auto (system theme), 1 = Dark, 2 = Light", SettingsServiceSource);
        Assert.Contains("NormalizeThemeSettings(settings);", SettingsServiceSource);
        Assert.Contains("settings.ThemeModeIndex = settings.ThemeModeIndex is >= 0 and <= 2 ? settings.ThemeModeIndex : 0;", SettingsServiceSource);

        Assert.Contains("[ObservableProperty] public partial int ThemeModeIndex { get; set; }", MainViewModelSource);
        Assert.Contains("ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(_settings.ThemeModeIndex);", MainViewModelSource);
        Assert.Contains("_settings.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(ThemeModeIndex);", MainViewModelSource);

        Assert.Contains("AppThemeMode", AppThemeServiceSource);
        Assert.Contains("ElementTheme.Default", AppThemeServiceSource);
        Assert.Contains("ElementTheme.Dark", AppThemeServiceSource);
        Assert.Contains("ElementTheme.Light", AppThemeServiceSource);
        Assert.Contains("ResolveEffectiveTheme", AppThemeServiceSource);

        Assert.Contains("Auto (system theme)", SettingsWindowSource);
        Assert.Contains("Dark mode", SettingsWindowSource);
        Assert.Contains("Light mode", SettingsWindowSource);
        Assert.Contains("_viewModel.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(themeMode.SelectedIndex);", SettingsWindowSource);
        Assert.Contains("RootGrid.ActualThemeChanged += (_, _) =>", SettingsWindowSource);
        Assert.Contains("ApplySettingsTitleBarButtonTheme();", SettingsWindowSource);

        Assert.Contains("ApplyAppTheme();", MainWindowWindowSource);
        Assert.Contains("RootGrid.ActualThemeChanged += (_, _) =>", MainWindowWindowSource);
        Assert.Contains("ApplyTitleBarButtonTheme();", MainWindowWindowSource);
        Assert.Contains("nameof(ViewModel.ThemeModeIndex)", MainWindowWindowSource);
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
        Assert.Contains("private bool _settingsContentBuilt;", SettingsWindowSource);
        Assert.Contains("private int? _pendingSelectTabIndex;", SettingsWindowSource);
        Assert.Contains("private DispatcherTimer? _deferredSettingsContentBuildTimer;", SettingsWindowSource);

        string constructor = ExtractMethod(SettingsWindowSource, "SettingsWindow", window: 3200);
        Assert.Contains("ShowSettingsLoadingPlaceholder();", constructor);
        Assert.DoesNotContain("BuildSettingsContent();", constructor);
        Assert.DoesNotContain("AttachSettingDirtyHandlers();", constructor);
        Assert.DoesNotContain("ExtractSearchableEntries();", constructor);

        string loaded = ExtractMethod(SettingsWindowSource, "RootGrid_Loaded", window: 1200);
        Assert.Contains("QueueDeferredSettingsContentBuild();", loaded);

        string loadingPlaceholder = ExtractMethod(SettingsWindowSource, "ShowSettingsLoadingPlaceholder", window: 1200);
        AssertContainsInOrder(loadingPlaceholder,
            "SearchBox.IsEnabled = false;",
            "TabList.IsEnabled = false;",
            "SettingsContent.Children.Clear();",
            "Text = \"Loading settings...\"");

        string deferredBuild = ExtractMethod(SettingsWindowSource, "BuildDeferredSettingsContent", window: 2600);
        AssertContainsInOrder(deferredBuild,
            "BuildSettingsContent();",
            "AttachSettingDirtyHandlers();",
            "_settingsContentBuilt = true;",
            "int selectedIndex = _pendingSelectTabIndex.GetValueOrDefault(0);",
            "SearchBox.IsEnabled = true;",
            "TabList.IsEnabled = true;",
            "TabList.SelectedIndex = selectedIndex;",
            "MarkSettingsClean();",
            "_settingDirtyTrackingEnabled = true;");

        string searchChanged = ExtractMethod(SettingsWindowSource, "OnSearchTextChanged", window: 3000);
        Assert.Contains("if (!_settingsContentBuilt) return;", searchChanged);
        AssertContainsInOrder(searchChanged,
            "TabList.Visibility = Visibility.Collapsed;",
            "EnsureSearchableEntriesExtracted();",
            "ClearSearchResultContainers();");

        string ensureSearchEntries = ExtractMethod(SettingsWindowSource, "EnsureSearchableEntriesExtracted", window: 800);
        AssertContainsInOrder(ensureSearchEntries,
            "if (_searchableEntriesExtracted)",
            "ExtractSearchableEntries();",
            "_searchableEntriesExtracted = true;");

        string collectControlText = ExtractMethod(SettingsWindowSource, "CollectControlSearchText", window: 1600);
        Assert.Contains("comboBox.Items.Count <= MaxComboBoxItemsIndexedForSettingsSearch", collectControlText);

        string attachMethod = ExtractMethod(SettingsWindowSource, "AttachSettingDirtyHandlers", window: 3000);
        Assert.Contains("textBox.TextChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("numberBox.ValueChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("comboBox.SelectionChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("checkBox.Checked += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("toggleSwitch.Toggled += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("calendarDatePicker.DateChanged += (_, _) => MarkSettingsDirty();", attachMethod);
        Assert.Contains("case ColorPicker:", attachMethod);
        Assert.DoesNotContain("colorPicker.ColorChanged += (_, _) => MarkSettingsDirty();", attachMethod);

        string dirtyMethod = ExtractMethod(SettingsWindowSource, "MarkSettingsDirty", window: 900);
        AssertContainsInOrder(dirtyMethod,
            "private void MarkSettingsDirty(bool requireValueChanges = true)",
            "if (!_settingDirtyTrackingEnabled || _settingsDirty)",
            "if (requireValueChanges && !HaveTrackedSettingValueChanges())",
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
            "if (HaveTrackedSettingValueChanges())",
            "MarkSettingsDirty();");

        string settingValueMethod = ExtractMethod(SettingsWindowSource, "TryGetSettingValue", window: 1600);
        Assert.Contains("value = numberBox.Value;", settingValueMethod);

        AssertContainsInOrder(SettingsWindowSource,
            "_viewModel.SuppressAdminWarning = false;",
            "MarkSettingsDirty(requireValueChanges: false);",
            "resetAdmin.IsEnabled = false;");
    }

    [Fact]
    public void SettingsWindow_CloseWithoutSaveRestoresLiveAppliedSettings()
    {
        Assert.Contains("Closed += OnSettingsWindowClosed;", SettingsWindowSource);

        string closedMethod = ExtractMethod(SettingsWindowSource, "OnSettingsWindowClosed", window: 900);
        AssertContainsInOrder(closedMethod,
            "_suppressOwnerHideToTray?.Invoke();",
            "RestoreUnsavedSettingsIfNeeded();",
            "_fontContrastCheckTimer?.Stop();",
            "_viewModel.PropertyChanged -= OnViewModelPropertyChanged;");

        string closeWithoutPrompt = ExtractMethod(SettingsWindowSource, "CloseSettingsWindowWithoutPrompt", window: 500);
        AssertContainsInOrder(closeWithoutPrompt,
            "_settingsCloseConfirmed = true;",
            "_suppressOwnerHideToTray?.Invoke();",
            "Close();");

        string restoreMethod = ExtractMethod(SettingsWindowSource, "RestoreUnsavedSettingsIfNeeded", window: 1800);
        AssertContainsInOrder(restoreMethod,
            "if (!HasSettingValueChanges())",
            "_settingDirtyTrackingEnabled = false;",
            "foreach (var (element, cleanValue) in _cleanSettingValues.ToArray())",
            "RestoreSettingValue(element, cleanValue);",
            "_settingsDirty = false;",
            "_settingDirtyTrackingEnabled = true;");

        string hasChangesMethod = ExtractMethod(SettingsWindowSource, "HasSettingValueChanges", window: 1600);
        AssertContainsInOrder(hasChangesMethod,
            "if (_settingsDirty)",
            "return HaveTrackedSettingValueChanges();");

        string trackedChangesMethod = ExtractMethod(SettingsWindowSource, "HaveTrackedSettingValueChanges", window: 1800);
        AssertContainsInOrder(trackedChangesMethod,
            "foreach (var element in _dirtyTrackedElements)",
            "TryGetSettingValue(element, out var currentValue)",
            "!Equals(cleanValue, currentValue)",
            "return true;");
        Assert.Contains("foreach (var lazyColor in _lazyColorSettings)", trackedChangesMethod);

        string restoreValueMethod = ExtractMethod(SettingsWindowSource, "RestoreSettingValue", window: 2400);
        Assert.Contains("textBox.Text = value as string ?? string.Empty;", restoreValueMethod);
        Assert.Contains("numberBox.Value = numberValue;", restoreValueMethod);
        Assert.Contains("comboBox.SelectedIndex = selectedIndex;", restoreValueMethod);
        Assert.Contains("checkBox.IsChecked = value is bool isChecked ? isChecked : null;", restoreValueMethod);
        Assert.Contains("toggleSwitch.IsOn = isOn;", restoreValueMethod);
        Assert.Contains("calendarDatePicker.Date = value is DateTimeOffset date ? date : null;", restoreValueMethod);
        Assert.Contains("colorPicker.Color = color;", restoreValueMethod);
    }

    [Fact]
    public void SettingsWindow_CloseWithoutChangesDoesNotPromptForNumberBoxText()
    {
        // Regression: closing Settings without any edits used to show the
        // "unsaved settings" prompt because the clean-value snapshot captured
        // NumberBox.Text, which is formatted asynchronously after the control
        // template loads. The captured baseline ("") then differed from the
        // settled display text (e.g. "0"), so HaveTrackedSettingValueChanges
        // returned true even though nothing changed. The snapshot must compare
        // only the committed numeric Value.
        string settingValueMethod = ExtractMethod(SettingsWindowSource, "TryGetSettingValue", window: 1600);
        Assert.Contains("value = numberBox.Value;", settingValueMethod);
        Assert.DoesNotContain("numberBox.Text", settingValueMethod);

        string restoreValueMethod = ExtractMethod(SettingsWindowSource, "RestoreSettingValue", window: 2400);
        Assert.Contains("case NumberBox numberBox when value is double numberValue:", restoreValueMethod);
        Assert.Contains("numberBox.Value = numberValue;", restoreValueMethod);
        Assert.DoesNotContain("numberBox.Text", restoreValueMethod);

        // Both close paths must gate the unsaved-changes prompt on a real change:
        // the AppWindow X button (OnSettingsAppWindowClosing) and the in-window
        // Close/Cancel button (RequestSettingsWindowCloseAsync).
        string appWindowClosing = ExtractMethod(SettingsWindowSource, "OnSettingsAppWindowClosing", window: 700);
        Assert.Contains("if (_settingsCloseConfirmed || !HasSettingValueChanges())", appWindowClosing);
        Assert.Contains("await ShowUnsavedSettingsClosePromptAsync();", appWindowClosing);

        string requestClose = ExtractMethod(SettingsWindowSource, "RequestSettingsWindowCloseAsync", window: 900);
        Assert.Contains("if (_settingsCloseConfirmed || !HasSettingValueChanges())", requestClose);
        Assert.Contains("CloseSettingsWindowWithoutPrompt();", requestClose);
    }

    [Fact]
    public void SettingsWindow_ResetButtonsDisableWhenAlreadyAtDefaults()
    {
        Assert.Contains("private readonly List<Action> _defaultResetButtonRefreshers = new();", SettingsWindowSource);

        string propertyChanged = ExtractMethod(SettingsWindowSource, "OnViewModelPropertyChanged", window: 900);
        Assert.Contains("RefreshDefaultResetButtons();", propertyChanged);

        string registerMethod = ExtractMethod(SettingsWindowSource, "RegisterDefaultResetButton", window: 900);
        AssertContainsInOrder(registerMethod,
            "void Refresh() => button.IsEnabled = !isCurrentDefault();",
            "_defaultResetButtonRefreshers.Add(Refresh);",
            "Refresh();");

        Assert.Contains("RegisterDefaultResetButton(reset, () => currentColor().Equals(fallback));", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(useDefault, () => SettingStringEquals(_viewModel.TerminalDefaultWorkingDirectory, string.Empty));", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(clearDateDefaults,", SettingsWindowSource);
        Assert.Contains("_viewModel.DefaultCreatedAfterDate is null", SettingsWindowSource);
        Assert.Contains("_viewModel.DefaultModifiedBeforeDate is null", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetAdminSeg,", SettingsWindowSource);
        Assert.Contains("SettingStringEquals(_viewModel.AdminProtectedPathSegments, AppSettings.DefaultAdminProtectedPathSegments)", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetBinaryExt,", SettingsWindowSource);
        Assert.Contains("SettingStringEquals(_viewModel.SettingsBinaryExtensions, AppSettings.DefaultBinaryExtensions)", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetResultMatchText,", SettingsWindowSource);
        Assert.Contains("SettingStringEquals(_viewModel.ResultListMatchTextFontFamily, AppSettings.DefaultResultListMatchTextFontFamily)", SettingsWindowSource);
        Assert.Contains("SettingColorEquals(", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetPreviewTextFont,", SettingsWindowSource);
        Assert.Contains("SettingStringEquals(_viewModel.PreviewTextFontFamily, AppSettings.DefaultPreviewTextFontFamily)", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetEditorFont,", SettingsWindowSource);
        Assert.Contains("SettingStringEquals(_viewModel.PreviewEditorFontFamily, AppSettings.DefaultPreviewEditorFontFamily)", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetFontContrastReminder,", SettingsWindowSource);
        Assert.Contains("!_viewModel.SuppressFontContrastWarnings && _viewModel.FontContrastReminderAfterUtc is null", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetAdmin, () => !_viewModel.SuppressAdminWarning);", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_CloseButtonCloses()
    {
        string closeMethod = ExtractMethod(SettingsWindowSource, "OnCloseClick");
        Assert.Contains("Close()", closeMethod);
    }

    [Theory]
    [InlineData("Search Defaults")]
    [InlineData("Search Limits")]
    [InlineData("Performance")]
    [InlineData("Display")]
    [InlineData("Editor")]
    [InlineData("Window")]
    [InlineData("Interaction")]
    [InlineData("Terminal Emulator")]
    [InlineData("Developer Options")]
    [InlineData("Shortcuts & History")]
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
    public void SearchDefaults_IncludeExcludePatternsUseWideTextBoxesAndModeSelectors()
    {
        Assert.Contains("Default include patterns (comma/semicolon-separated):", SettingsWindowSource);
        Assert.Contains("Default exclude patterns (comma/semicolon-separated):", SettingsWindowSource);
        Assert.Contains("var incMode = new ComboBox", SettingsWindowSource);
        Assert.Contains("var excMode = new ComboBox", SettingsWindowSource);
        Assert.Contains("_viewModel.IncludeFilterModeIndex = incMode.SelectedIndex;", SettingsWindowSource);
        Assert.Contains("_viewModel.ExcludeFilterModeIndex = excMode.SelectedIndex;", SettingsWindowSource);
        Assert.Contains("PlaceholderText = _viewModel.IncludeFilterPlaceholder, Width = 620", SettingsWindowSource);
        Assert.Contains("PlaceholderText = _viewModel.ExcludeFilterPlaceholder, Width = 620", SettingsWindowSource);

        Assert.Contains("public string IncludeGlobs", SettingsServiceSource);
        Assert.Contains("public string ExcludeGlobs", SettingsServiceSource);
        Assert.Contains("public int IncludeFilterModeIndex", SettingsServiceSource);
        Assert.Contains("public int ExcludeFilterModeIndex", SettingsServiceSource);
        Assert.DoesNotContain("[JsonIgnore] public string IncludeGlobs", SettingsServiceSource);
        Assert.DoesNotContain("[JsonIgnore] public string ExcludeGlobs", SettingsServiceSource);

        Assert.Contains("IncludeFilterModeIndex = _settings.IncludeFilterModeIndex", MainViewModelSource);
        Assert.Contains("ExcludeFilterModeIndex = _settings.ExcludeFilterModeIndex", MainViewModelSource);
        Assert.Contains("_settings.IncludeFilterModeIndex = IncludeFilterModeIndex", MainViewModelSource);
        Assert.Contains("_settings.ExcludeFilterModeIndex = ExcludeFilterModeIndex", MainViewModelSource);
        Assert.Contains("SelectedIndex=\"{x:Bind ViewModel.IncludeFilterModeIndex, Mode=TwoWay}\"", MainWindowXaml);
        Assert.Contains("SelectedIndex=\"{x:Bind ViewModel.ExcludeFilterModeIndex, Mode=TwoWay}\"", MainWindowXaml);
    }

    [Fact]
    public void SettingsWindow_WordWrapToggleCallsApplyWordWrap()
    {
        Assert.Contains("_applyWordWrap?.Invoke(true)", SettingsWindowSource);
        Assert.Contains("_applyWordWrap?.Invoke(false)", SettingsWindowSource);
    }

    [Fact]
    public void ContentSearchParallelism_DefaultsToAllCoresWithoutAutoLabel()
    {
        Assert.Contains("public int ParallelismIndex { get; set; } = 4; // 0 = safe cap", SettingsServiceSource);
        Assert.Contains("[ObservableProperty] public partial int ParallelismIndex { get; set; } = 4; // 0 = safe cap", MainViewModelSource);

        int start = SettingsWindowSource.IndexOf("Content-search parallelism (concurrent file scan threads):", StringComparison.Ordinal);
        int end = SettingsWindowSource.IndexOf("var hddToggle", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        string parallelismBlock = SettingsWindowSource[start..end];
        Assert.Contains("Safe cap (up to", parallelismBlock);
        Assert.Contains("All cores", parallelismBlock);
        Assert.DoesNotContain("Auto", parallelismBlock);
        Assert.Contains("parallelism.SelectedIndex = _viewModel.ParallelismIndex;", parallelismBlock);
    }

    [Fact]
    public void SettingsWindow_FontSelectorsUseSystemFontPreviewPicker()
    {
        Assert.Contains("CanvasTextFormat.GetSystemFontFamilies()", SettingsWindowSource);
        Assert.Contains("CreateFontFamilyPicker(", SettingsWindowSource);
        Assert.Contains("new XamlFontFamily(fontFamily)", SettingsWindowSource);
        Assert.DoesNotContain("var resultMatchFontFamily = new TextBox", SettingsWindowSource);
        Assert.DoesNotContain("var previewTextFontFamily = new TextBox", SettingsWindowSource);
        Assert.DoesNotContain("var editorFontFamily = new TextBox", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_ConfigurableFontSurfacesExposeFamilyAndSizeTogether()
    {
        string displayBlock = SettingsWindowSource[
            SettingsWindowSource.IndexOf("// ── Display ──", StringComparison.Ordinal)..
            SettingsWindowSource.IndexOf("// ── Editor ──", StringComparison.Ordinal)];

        AssertContainsInOrder(displayBlock,
            "Results list match text",
            "var resultMatchFontFamily = CreateFontFamilyPicker(",
            "var resultMatchFontSize = new NumberBox");
        AssertContainsInOrder(displayBlock,
            "Preview text font",
            "var previewTextFontFamily = CreateFontFamilyPicker(",
            "var previewTextFontSize = new NumberBox");
        AssertContainsInOrder(displayBlock,
            "Built-in editor font",
            "var editorFontFamily = CreateFontFamilyPicker(",
            "var editorFontSize = new NumberBox");

        Assert.Contains("public string PreviewTextFontFamily { get; set; } = DefaultPreviewTextFontFamily;", SettingsServiceSource);
        Assert.Contains("public int PreviewTextFontSize { get; set; } = DefaultPreviewTextFontSize;", SettingsServiceSource);
        Assert.Contains("[ObservableProperty] public partial string PreviewTextFontFamily", MainViewModelSource);
        Assert.Contains("[ObservableProperty] public partial int PreviewTextFontSize", MainViewModelSource);
    }

    [Fact]
    public void SettingsWindow_DisplayAppearanceSettingsAreGroupedBySurface()
    {
        Assert.Contains("AddSettingsGroupBox", SettingsWindowSource);
        AssertContainsInOrder(SettingsWindowSource,
            "var g = AddTab(\"Display\");",
            "var appAppearanceGroup = AddSettingsGroupBox(g, \"Application Appearance\");",
            "var fileMatchListGroup = AddSettingsGroupBox(g, \"File Match List\");",
            "var previewViewerGroup = AddSettingsGroupBox(g, \"Preview Viewer\");",
            "var editorAppearanceGroup = AddSettingsGroupBox(g, \"Built-In Editor Appearance\");");

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
        Assert.Contains("Preview text font", displayBlock);
        Assert.Contains("Preview font colors", displayBlock);
        Assert.Contains("_viewModel.PreviewTextFontFamily", displayBlock);
        Assert.Contains("_viewModel.PreviewEditorFontFamily", displayBlock);
        Assert.Contains("Editor gutter text:", displayBlock);

        Assert.DoesNotContain("CaptureTabPageRootElements();", SettingsWindowSource);
        Assert.DoesNotContain("OriginalPlacements = capturedPlacements", SettingsWindowSource);
        Assert.DoesNotContain("RestoreOriginalPlacements(entry.OriginalPlacements)", SettingsWindowSource);
        AssertContainsInOrder(SettingsWindowSource,
            "if (child is Border { Child: UIElement groupChild })",
            "EnumerateSearchableGroupChild(groupChild)");

        Assert.DoesNotContain("_viewModel.PreviewEditorFontFamily", editorTabBlock);
        Assert.DoesNotContain("Editor gutter text:", editorTabBlock);
    }

    [Fact]
    public void SettingsWindow_HotkeySection_QueriesAvailableKeys()
    {
        int shortcutsStart = SettingsWindowSource.IndexOf("// ── Shortcuts & History ──", StringComparison.Ordinal);
        Assert.True(shortcutsStart >= 0, "Shortcuts & History block not found.");
        string shortcutsBlock = SettingsWindowSource[shortcutsStart..];
        Assert.DoesNotContain("GetAvailableCtrlShiftLetterKeys(_mainHwnd)", shortcutsBlock);
        Assert.Contains("QueueHotkeyAvailabilityLoad(hotkey, hotkeyCombo, hotkeyAvailabilityStatus);", shortcutsBlock);
        Assert.Contains("Checking available shortcuts...", shortcutsBlock);

        string queueMethod = ExtractMethod(SettingsWindowSource, "QueueHotkeyAvailabilityLoad", window: 1600);
        AssertContainsInOrder(queueMethod,
            "Task.Run(() => _hotkeyService.GetAvailableCtrlShiftLetterKeys(hwnd))",
            "DispatcherQueue.TryEnqueue",
            "ApplyHotkeyAvailability(task.Result, hotkey, hotkeyCombo, availabilityStatus);");

        string applyMethod = ExtractMethod(SettingsWindowSource, "ApplyHotkeyAvailability", window: 3600);
        AssertContainsInOrder(applyMethod,
            "bool wasDirtyTrackingEnabled = _settingDirtyTrackingEnabled;",
            "_settingDirtyTrackingEnabled = false;",
            "HotkeyService.ChooseAvailableKey(availableHotkeyKeys, _viewModel.GlobalHotkeyKey);",
            "hotkey.IsEnabled = availableHotkeyKeys.Count > 0;",
            "hotkeyCombo.Items.Clear();",
            "HotkeyService.FormatCtrlShift(key)",
            "_cleanSettingValues[hotkeyCombo] = hotkeyComboValue;",
            "_settingDirtyTrackingEnabled = wasDirtyTrackingEnabled;");
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
            "var g = AddTab(\"Interaction\");",
            "var g = AddTab(\"Terminal Emulator\");",
            "AddTerminalEmulationSetting(workingDirectoryGroup);",
            "var g = AddTab(\"Developer Options\");");

        int uiBehaviorStart = SettingsWindowSource.IndexOf("var g = AddTab(\"Interaction\");", StringComparison.Ordinal);
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
        Assert.Contains("string workingDirectory = ResolveTerminalWorkingDirectory();", MainWindowTerminalSource);
        Assert.Contains("workingDirectory: workingDirectory", MainWindowTerminalSource);
        Assert.Contains("TryResolveExistingDirectory(ViewModel.TerminalDefaultWorkingDirectory", MainWindowTerminalSource);
        Assert.Contains("TryResolveExistingDirectory(App.LaunchWorkingDirectory", MainWindowTerminalSource);
        Assert.Contains("Start(int cols = 120, int rows = 30, string? workingDirectory = null)", ConPtyTerminalServiceSource);
        Assert.Contains("ResolveCommandShellExecutable()", ConPtyTerminalServiceSource);
        Assert.Contains("Path.Combine(system, \"cmd.exe\")", ConPtyTerminalServiceSource);
        Assert.Contains("FindExecutableOnPath(\"cmd.exe\")", ConPtyTerminalServiceSource);
        Assert.Contains("new ProcessStartInfo", ConPtyTerminalServiceSource);
        Assert.Contains("RedirectStandardInput = true", ConPtyTerminalServiceSource);
        Assert.Contains("RedirectStandardOutput = true", ConPtyTerminalServiceSource);
        Assert.Contains("RedirectStandardError = true", ConPtyTerminalServiceSource);
        Assert.Contains("startInfo.ArgumentList.Add(\"/Q\");", ConPtyTerminalServiceSource);
        Assert.Contains("startInfo.ArgumentList.Add(\"/K\");", ConPtyTerminalServiceSource);
    }

    [Fact]
    public void TerminalStartup_IsTransactionalAndReportsFailures()
    {
        AssertContainsInOrder(MainWindowTerminalSource,
            "terminalService.OutputReceived += text => OnTerminalOutput(text, sessionGeneration);",
            "terminalService.ProcessExited += exitCode => OnTerminalProcessExited(exitCode, sessionGeneration);",
            "_terminalService = terminalService;",
            "try",
            "string workingDirectory = ResolveTerminalWorkingDirectory();",
            "terminalService.Start(cols: _terminalColumns, rows: _terminalRows, workingDirectory: workingDirectory);");
        Assert.Contains("terminalService.Start(cols: _terminalColumns, rows: _terminalRows, workingDirectory: workingDirectory);", MainWindowTerminalSource);
        Assert.Contains("_terminalService = terminalService;", MainWindowTerminalSource);
        Assert.Contains("if (ReferenceEquals(_terminalService, terminalService))", MainWindowTerminalSource);
        Assert.Contains("_terminalService = null;", MainWindowTerminalSource);
        Assert.Contains("LogService.Instance.Warning(\"Terminal\", \"Failed to start terminal shell session\", ex);", MainWindowTerminalSource);
        Assert.Contains("Terminal shell session started: shellPid=", MainWindowTerminalSource);
        Assert.Contains("[Terminal failed to start:", MainWindowTerminalSource);

        string startMethod = ExtractMethod(ConPtyTerminalServiceSource, "Start", window: 2800);
        Assert.Contains("try", startMethod);
        Assert.Contains("catch", startMethod);
        Assert.Contains("_process = new Process", startMethod);
        Assert.Contains("_process.Start()", startMethod);
        Assert.Contains("Dispose();", startMethod);
        Assert.Contains("throw;", startMethod);
        Assert.Contains("public int ProcessId { get; private set; }", ConPtyTerminalServiceSource);
        Assert.Contains("BuildLocalEcho(text)", ConPtyTerminalServiceSource);
        Assert.Contains("NormalizeShellLineEndings(text)", ConPtyTerminalServiceSource);
        Assert.Contains("_input.Write(shellText);", ConPtyTerminalServiceSource);
        Assert.Contains("ReadRedirectedOutput", ConPtyTerminalServiceSource);
        Assert.Contains("First shell output received", ConPtyTerminalServiceSource);
        Assert.Contains("First terminal input written", ConPtyTerminalServiceSource);
    }

    [Fact]
    public void EmbeddedTerminal_UsesVirtualHostAndLogsStartupFailures()
    {
        Assert.Contains("\"yagu-terminal\", assetsDir", MainWindowTerminalSource);
        Assert.Contains("Navigate(\"https://yagu-terminal/terminal.html\")", MainWindowTerminalSource);
        Assert.DoesNotContain("new Uri(terminalHtmlPath).AbsoluteUri", MainWindowTerminalSource);
        Assert.Contains("AttachTerminalWebViewDiagnostics(TerminalWebView.CoreWebView2);", MainWindowTerminalSource);
        Assert.Contains("coreWebView.NavigationCompleted += OnTerminalNavigationCompleted;", MainWindowTerminalSource);
        Assert.Contains("coreWebView.ProcessFailed += OnTerminalWebViewProcessFailed;", MainWindowTerminalSource);
        Assert.Contains("coreWebView.WebResourceResponseReceived += OnTerminalWebResourceResponseReceived;", MainWindowTerminalSource);
        Assert.Contains("Terminal WebView initialization failed", MainWindowTerminalSource);
        Assert.Contains("IsTabStop=\"True\"", MainWindowXaml);
        Assert.Contains("Terminal page reported ready", MainWindowTerminalSource);
        Assert.Contains("if (_terminalPaneExpanded)", MainWindowTerminalSource);
        Assert.Contains("FocusTerminal();", MainWindowTerminalSource);
        Assert.Contains("Posted first terminal output to WebView", MainWindowTerminalSource);
        Assert.Contains("FilterTerminalOutputForXterm(text)", MainWindowTerminalSource);
        Assert.Contains("NudgeCommandShellPromptAfterStartupControlPacket(text);", MainWindowTerminalSource);
        Assert.Contains("_terminalService?.WriteInput(\"\\r\");", MainWindowTerminalSource);
        Assert.Contains("Terminal host received first input", MainWindowTerminalSource);
        Assert.Contains("Terminal input received before the shell session was available", MainWindowTerminalSource);
        Assert.Contains("\\u001b[?9001h", MainWindowTerminalSource);
        Assert.Contains("\\u001b[?1004h", MainWindowTerminalSource);
        Assert.Contains("Terminal output post failed", MainWindowTerminalSource);
        Assert.Contains("case \"hostLog\":", MainWindowTerminalSource);
        Assert.Contains("LogTerminalPageMessage(root);", MainWindowTerminalSource);

        Assert.Contains("function postHostMessage(message)", TerminalHtml);
        Assert.Contains("function normalizeHostMessage(data)", TerminalHtml);
        Assert.Contains("return JSON.parse(data);", TerminalHtml);
        Assert.Contains("var msg = normalizeHostMessage(event.data);", TerminalHtml);
        Assert.Contains("function refreshTerminalRows()", TerminalHtml);
        Assert.Contains("term.refresh(0, term.rows - 1);", TerminalHtml);
        Assert.Contains("function scheduleInitialTerminalPaintRefresh()", TerminalHtml);
        Assert.Contains("window.setTimeout(refreshTerminalRows, 150);", TerminalHtml);
        Assert.Contains("function signalReadyWhenMeasured(attempt)", TerminalHtml);
        Assert.Contains("Terminal page measured", TerminalHtml);
        Assert.Contains("function isTerminalFocusReport(data)", TerminalHtml);
        Assert.Contains("Terminal page sent first input", TerminalHtml);
        Assert.Contains("terminalElement.tabIndex = 0;", TerminalHtml);
        Assert.Contains("function focusTerminal()", TerminalHtml);
        Assert.Contains("terminalElement.addEventListener('pointerdown'", TerminalHtml);
        Assert.Contains("terminalElement.addEventListener('click'", TerminalHtml);
        Assert.Contains("window.addEventListener('focus'", TerminalHtml);
        Assert.Contains("function forwardEnterKey(event)", TerminalHtml);
        Assert.Contains("term.attachCustomKeyEventHandler", TerminalHtml);
        Assert.Contains("function translateKeyEventToInput(event)", TerminalHtml);
        Assert.Contains("document.addEventListener('paste'", TerminalHtml);
        Assert.Contains("submitInputLine();", TerminalHtml);
        Assert.Contains("sendTerminalInput(line + '\\r', false);", TerminalHtml);
        Assert.Contains("term.focus();", TerminalHtml);
        Assert.Contains("term.writeSync(msg.data || '')", TerminalHtml);
        Assert.Contains("type: 'hostLog'", TerminalHtml);
        Assert.Contains("Terminal page failed to initialize", TerminalHtml);
        Assert.Contains("Terminal page received first output", TerminalHtml);
        Assert.Contains("#terminal .xterm .xterm-viewport", TerminalHtml);
        Assert.Contains("#terminal .xterm .xterm-screen", TerminalHtml);
        Assert.Contains("function safeFit()", TerminalHtml);
        Assert.Contains("window.requestAnimationFrame(function()", TerminalHtml);
        Assert.Contains("signalReadyWhenMeasured(0);", TerminalHtml);
        Assert.Contains("postHostMessage({ type: 'ready' });", TerminalHtml);
        Assert.DoesNotContain("window.chrome.webview.postMessage(JSON.stringify({ type: 'ready' }))", TerminalHtml);
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
        Assert.Contains("SuppressLauncherHideToTrayForOwnedWindowClose", helper);
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
        Assert.Contains("SuppressLauncherHideToTrayForOwnedWindowClose();", method);
        Assert.Contains("_settingsWindow = null", method);
    }

    [Fact]
    public void SettingsWindow_SelectTab_RestoresNormalTabViewBeforeSelecting()
    {
        string method = ExtractMethod(SettingsWindowSource, "SelectTab", window: 2200);
        AssertContainsInOrder(method,
            "if (index < 0)",
            "if (!_settingsContentBuilt)",
            "_pendingSelectTabIndex = index;",
            "if (index >= TabList.Items.Count)");
        Assert.Contains("_isSearchActive", method);
        Assert.Contains("SearchBox.Text = string.Empty;", method);
        Assert.Contains("TabList.Visibility = Visibility.Visible;", method);
        Assert.Contains("ClearSearchResultContainers();", method);
        Assert.DoesNotContain("RestoreTabPageElements();", method);
        AssertContainsInOrder(method,
            "TabList.SelectedIndex = index;",
            "SettingsContent.Children.Clear();",
            "AddSettingsContentChild(_tabPages[index]);");

        string tabSelectionChanged = ExtractMethod(SettingsWindowSource, "OnTabSelectionChanged", window: 900);
        Assert.Contains("if (!_settingsContentBuilt) return;", tabSelectionChanged);
    }

    [Fact]
    public void SettingsWindow_SearchRendersFreshRowsInsteadOfReparentingControls()
    {
        string searchChanged = ExtractMethod(SettingsWindowSource, "OnSearchTextChanged", window: 2600);
        Assert.Contains("CreateSearchResultEntry(entry);", searchChanged);
        Assert.DoesNotContain("new HashSet<UIElement>()", searchChanged);
        Assert.DoesNotContain("entry.BuildElements()", searchChanged);
        Assert.DoesNotContain("TryRegisterRenderedElement", searchChanged);
        Assert.DoesNotContain("DetachAllTabPageElements();", searchChanged);
        Assert.DoesNotContain("RestoreTabPageElements();", searchChanged);

        string clearContainers = ExtractMethod(SettingsWindowSource, "ClearSearchResultContainers", window: 800);
        AssertContainsInOrder(clearContainers,
            "DetachFromParent(container);",
            "if (container is Panel panel)",
            "panel.Children.Clear();",
            "_searchResultContainers.Clear();");

        string resultEntry = ExtractMethod(SettingsWindowSource, "CreateSearchResultEntry", window: 2400);
        Assert.Contains("return new Border", resultEntry);
        Assert.Contains("Text = entry.Label", resultEntry);
        Assert.Contains("Text = entry.Description", resultEntry);
        Assert.Contains("Text = TrimSearchResultText(entry.ControlText)", resultEntry);
        Assert.Contains("Glyph = \"\\uE8A7\"", resultEntry);
        Assert.Contains("FontFamily = new XamlFontFamily(\"Segoe MDL2 Assets\")", resultEntry);
        Assert.Contains("ToolTipService.SetToolTip(jumpButton, \"Open section\");", resultEntry);
        Assert.Contains("AutomationProperties.SetName(jumpButton, \"Open section\");", resultEntry);

        Assert.DoesNotContain("OriginalPlacements", SettingsWindowSource);
        Assert.DoesNotContain("BuildElements", SettingsWindowSource);
        Assert.DoesNotContain("RestoreOriginalPlacements", SettingsWindowSource);
        Assert.DoesNotContain("TryRegisterRenderedElement", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_FontFamilyPickersLazyLoadInstalledFonts()
    {
        string createPicker = ExtractMethod(SettingsWindowSource, "CreateFontFamilyPicker", window: 2600);
        Assert.Contains("BuildInitialFontFamilyOptions(selectedValue, defaultValue)", createPicker);
        Assert.Contains("picker.DropDownOpened +=", createPicker);
        Assert.Contains("PopulateFontFamilyPicker(picker, defaultValue);", createPicker);
        Assert.DoesNotContain("GetSystemFontFamilyNames()", createPicker);

        string populatePicker = ExtractMethod(SettingsWindowSource, "PopulateFontFamilyPicker", window: 1200);
        Assert.Contains("BuildFontFamilyOptions(selectedValue, defaultValue)", populatePicker);
        Assert.Contains("FindFontFamilyItem(picker, fontFamily) is null", populatePicker);

        string buildInitial = ExtractMethod(SettingsWindowSource, "BuildInitialFontFamilyOptions", window: 700);
        Assert.Contains("AddFontFamilyOption(options, currentValue);", buildInitial);
        Assert.Contains("AddFontFamilyOption(options, defaultValue);", buildInitial);
        Assert.DoesNotContain("GetSystemFontFamilyNames()", buildInitial);
    }

    [Fact]
    public void SettingsWindow_ColorPickersLoadInFlyouts()
    {
        string addColorSetting = ExtractMethod(SettingsWindowSource, "AddColorSetting", window: 9000);
        Assert.Contains("ColorPicker? picker = null;", addColorSetting);
        Assert.Contains("var colorState = new LazyColorSettingState", addColorSetting);
        Assert.Contains("_lazyColorSettings.Add(colorState);", addColorSetting);
        Assert.Contains("editColorButton.Flyout = new Flyout { Content = picker };", addColorSetting);
        Assert.Contains("parent.Children.Add(editColorButton);", addColorSetting);
        Assert.Contains("refreshColorButton();", addColorSetting);
        Assert.Contains("MarkSettingsDirty();", addColorSetting);
        Assert.DoesNotContain("parent.Children.Add(picker);", addColorSetting);
    }

    [Fact]
    public void SettingsWindow_CloseWithUnsavedChangesShowsTitlelessModal()
    {
        Assert.Contains("appWindow.Closing += OnSettingsAppWindowClosing;", SettingsWindowSource);
        Assert.Contains("_appWindow.Closing -= OnSettingsAppWindowClosing;", SettingsWindowSource);

        string closeButton = ExtractMethod(SettingsWindowSource, "OnCloseClick", window: 500);
        Assert.Contains("await RequestSettingsWindowCloseAsync();", closeButton);

        string appClosing = ExtractMethod(SettingsWindowSource, "OnSettingsAppWindowClosing", window: 1200);
        AssertContainsInOrder(appClosing,
            "if (_settingsCloseConfirmed || !HasSettingValueChanges())",
            "args.Cancel = true;",
            "await ShowUnsavedSettingsClosePromptAsync();");

        string prompt = ExtractMethod(SettingsWindowSource, "ShowUnsavedSettingsClosePromptAsync", window: 3600);
        AssertContainsInOrder(prompt,
            "if (_settingsClosePromptOpen)",
            "YaguDialog.ShowAsync(",
            "_settingsHwnd",
            "Title = \"Unsaved settings\"",
            "PrimaryButtonText = \"Save and close\"",
            "SecondaryButtonText = \"Discard changes\"",
            "CloseButtonText = \"Keep editing\"",
            "ShowTitle = false",
            "ShowTitleBar = false",
            "ShowTopRightCloseButton = true");
        AssertContainsInOrder(prompt,
            "if (result == YaguDialogResult.Primary)",
            "await _viewModel.PersistSettingsAsync();",
            "MarkSettingsClean();",
            "CloseSettingsWindowWithoutPrompt();",
            "else if (result == YaguDialogResult.Secondary)",
            "RestoreUnsavedSettingsIfNeeded();",
            "CloseSettingsWindowWithoutPrompt();");

        Assert.Contains("public bool ShowTopRightCloseButton { get; init; }", YaguDialogSource);
        Assert.Contains("if (options.ShowTopRightCloseButton)", YaguDialogSource);
        Assert.Contains("ToolTipService.SetToolTip(topRightClose, \"Close\");", YaguDialogSource);
        Assert.Contains("topRightClose.Click += (_, _) => Complete(YaguDialogResult.Close);", YaguDialogSource);
    }

    [Fact]
    public void MainWindow_FirstTimeIntroductoryTooltipsUseClosableTeachingTip()
    {
        Assert.Contains("<TeachingTip x:Name=\"IntroTeachingTip\"", MainWindowXaml);
        Assert.Contains("CloseButtonContent=\"Got it\"", MainWindowXaml);
        Assert.Contains("ShouldConstrainToRootBounds=\"True\"", MainWindowXaml);
        Assert.Contains("Loaded=\"OnFileGroupHeaderLoaded\"", MainWindowXaml);
        Assert.Contains("Loaded=\"OnMatchLineNumberLoaded\"", MainWindowXaml);

        Assert.Contains("Double click or right click to preview this file", MainWindowIntroTipsSource);
        Assert.Contains("private static readonly TimeSpan FileDrawerIntroTipDelay = TimeSpan.FromSeconds(2);", MainWindowIntroTipsSource);
        Assert.Contains("QueueDelayedFileDrawerIntroTip(target);", MainWindowIntroTipsSource);
        Assert.Contains("new DispatcherTimer { Interval = FileDrawerIntroTipDelay }", MainWindowIntroTipsSource);
        Assert.Contains("TryOpenIntroTip(\r\n                IntroTipKind.FileDrawer", MainWindowIntroTipsSource);
        Assert.Contains("Select a line number to preview just that line number + context", MainWindowIntroTipsSource);
        Assert.Contains("Double click on any match to jump to it in a file editor", MainWindowIntroTipsSource);
        Assert.Contains("IntroTeachingTip.Target = target;", MainWindowIntroTipsSource);
        Assert.Contains("IntroTeachingTip.IsOpen = true;", MainWindowIntroTipsSource);
        Assert.Contains("_ = MarkIntroTipShownAsync(kind);", MainWindowIntroTipsSource);
        Assert.Contains("CloseButtonContent=\"Got it\"", MainWindowXaml);

        Assert.Contains("TryShowPreviewMatchIntroTip();", MainWindowMatchNavSource);
        Assert.Contains("ActiveMatchOverlay.Visibility = Visibility.Visible;\r\n            TryShowPreviewMatchIntroTip();\r\n            LogWordWrapOverlayDiagnostic(", MainWindowMatchNavSource);
    }

    [Fact]
    public void SettingsWindow_CanResetFirstTimeIntroductoryTooltipsWithoutImmediatePersistence()
    {
        Assert.Contains("public bool HasShownFileDrawerIntroTip { get; set; }", SettingsServiceSource);
        Assert.Contains("public bool HasShownFileDrawerLineNumberIntroTip { get; set; }", SettingsServiceSource);
        Assert.Contains("public bool HasShownPreviewMatchIntroTip { get; set; }", SettingsServiceSource);

        Assert.Contains("[ObservableProperty] public partial bool HasShownFileDrawerIntroTip { get; set; }", MainViewModelSource);
        Assert.Contains("[ObservableProperty] public partial bool HasShownFileDrawerLineNumberIntroTip { get; set; }", MainViewModelSource);
        Assert.Contains("[ObservableProperty] public partial bool HasShownPreviewMatchIntroTip { get; set; }", MainViewModelSource);
        Assert.Contains("public void ResetFirstTimeIntroductoryTooltips()", MainViewModelSource);
        Assert.Contains("public void RestoreFirstTimeIntroductoryTooltips(bool fileDrawer, bool fileDrawerLineNumber, bool previewMatch)", MainViewModelSource);
        Assert.Contains("public Task MarkFileDrawerIntroTipShownAsync()", MainViewModelSource);
        Assert.Contains("_settings.HasShownFileDrawerIntroTip = HasShownFileDrawerIntroTip;", MainViewModelSource);
        Assert.Contains("_settings.HasShownFileDrawerLineNumberIntroTip = HasShownFileDrawerLineNumberIntroTip;", MainViewModelSource);
        Assert.Contains("_settings.HasShownPreviewMatchIntroTip = HasShownPreviewMatchIntroTip;", MainViewModelSource);

        Assert.Contains("Content = \"Reset first-time introductory tooltips\"", SettingsWindowSource);
        Assert.Contains("_viewModel.ResetFirstTimeIntroductoryTooltips();", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetFirstTimeIntroTips, AreFirstTimeIntroductoryTooltipsReset);", SettingsWindowSource);

        int resetClickStart = SettingsWindowSource.IndexOf("resetFirstTimeIntroTips.Click", StringComparison.Ordinal);
        Assert.True(resetClickStart >= 0, "Reset first-time intro tips click handler not found.");
        int resetClickEnd = SettingsWindowSource.IndexOf("RegisterDefaultResetButton(resetFirstTimeIntroTips", resetClickStart, StringComparison.Ordinal);
        Assert.True(resetClickEnd > resetClickStart, "Reset first-time intro tips click handler end not found.");
        string resetClick = SettingsWindowSource[resetClickStart..resetClickEnd];
        AssertContainsInOrder(resetClick,
            "_viewModel.ResetFirstTimeIntroductoryTooltips();",
            "MarkSettingsDirty();");

        string propertyChanged = ExtractMethod(SettingsWindowSource, "OnViewModelPropertyChanged", window: 900);
        AssertContainsInOrder(propertyChanged,
            "CaptureCleanFirstTimeIntroductoryTooltipValues();",
            "RefreshDefaultResetButtons();");

        string captureClean = ExtractMethod(SettingsWindowSource, "CaptureCleanSettingValues", window: 1000);
        Assert.Contains("CaptureCleanFirstTimeIntroductoryTooltipValues();", captureClean);

        string restoreUnsaved = ExtractMethod(SettingsWindowSource, "RestoreUnsavedSettingsIfNeeded", window: 1400);
        Assert.Contains("RestoreCleanFirstTimeIntroductoryTooltipValues();", restoreUnsaved);

        string trackedChanges = ExtractMethod(SettingsWindowSource, "HaveTrackedSettingValueChanges", window: 2200);
        Assert.Contains("return HaveFirstTimeIntroductoryTooltipValuesChanged();", trackedChanges);
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
        Assert.Contains("IsLauncherHideTemporarilySuppressed()", helper);
        Assert.Contains("_settingsWindow is not null", helper);
        Assert.Contains("_helpWindow is not null", helper);
        Assert.Contains("_ownedModalWindowDepth > 0", helper);

        string suppress = ExtractMethod(MainWindowLauncherSource, "SuppressLauncherHideToTrayForOwnedWindowClose", window: 500);
        Assert.Contains("DateTimeOffset.UtcNow.AddSeconds(10)", suppress);
        Assert.Contains("private DateTimeOffset _suppressLauncherHideUntilUtc;", MainWindowWindowSource);

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
    public void F1Shortcut_OpensHelpFromMainSettingsAndTerminalFocus()
    {
        Assert.Contains("PreviewKeyDown=\"OnRootGridPreviewKeyDown\"", MainWindowXaml);
        Assert.Contains("InitializeHelpKeyboardShortcut();", MainWindowWindowSource);

        string mainHelpAccelerator = ExtractMethod(MainWindowKeyboardSource, "InitializeHelpKeyboardShortcut", window: 1000);
        Assert.Contains("Key = VirtualKey.F1", mainHelpAccelerator);
        Assert.Contains("OpenHelpWindow();", mainHelpAccelerator);

        string mainKeyPreview = ExtractMethod(MainWindowKeyboardSource, "OnRootGridPreviewKeyDown", window: 900);
        Assert.Contains("VirtualKey.F1", mainKeyPreview);
        Assert.Contains("OpenHelpWindow();", mainKeyPreview);

        Assert.Contains("IsHelpShortcutMessage(message, wParam)", MainWindowLauncherSource);
        Assert.Contains("message is WmKeyDown or WmSysKeyDown", MainWindowLauncherSource);
        Assert.Contains("wParam.ToUInt32() == VkF1", MainWindowLauncherSource);
        Assert.Contains("private const uint VkF1 = 0x70;", MainWindowWindowSource);

        Assert.Contains("PreviewKeyDown=\"OnSettingsRootPreviewKeyDown\"", SettingsWindowXaml);
        Assert.Contains("Action openHelp", SettingsWindowSource);
        Assert.Contains("InitializeHelpKeyboardShortcut();", SettingsWindowSource);
        Assert.Contains("Key = VirtualKey.F1", SettingsWindowSource);
        Assert.Contains("_openHelp();", SettingsWindowSource);
        Assert.Contains("OpenHelpWindow", MainWindowSource);

        Assert.Contains("case \"openHelp\":", MainWindowTerminalSource);
        Assert.Contains("OpenHelpWindow();", MainWindowTerminalSource);
        Assert.Contains("event.key === 'F1'", TerminalHtml);
        Assert.Contains("type: 'openHelp'", TerminalHtml);
    }

    [Fact]
    public void EmbeddedTerminal_ContextMenuResetsTerminalSession()
    {
        Assert.Contains("id=\"terminalMenu\"", TerminalHtml);
        Assert.Contains("Reset terminal session", TerminalHtml);
        Assert.Contains("terminalElement.addEventListener('contextmenu', showTerminalMenu);", TerminalHtml);
        Assert.Contains("term.reset();", TerminalHtml);
        Assert.Contains("type: 'resetTerminal'", TerminalHtml);

        Assert.Contains("case \"resetTerminal\":", MainWindowTerminalSource);
        Assert.Contains("ResetTerminalSession();", MainWindowTerminalSource);
        Assert.Contains("private void ResetTerminalSession()", MainWindowTerminalSource);
        Assert.Contains("DisposeTerminal();", MainWindowTerminalSource);
        Assert.Contains("StartConPtySession();", MainWindowTerminalSource);
        Assert.Contains("_terminalSessionGeneration", MainWindowTerminalSource);
    }

    [Fact]
    public void EmbeddedTerminal_ContextMenuProvidesClipboardAndClearCommands()
    {
        Assert.Contains("id=\"terminalCopy\"", TerminalHtml);
        Assert.Contains("id=\"terminalPaste\"", TerminalHtml);
        Assert.Contains("id=\"terminalCut\"", TerminalHtml);
        Assert.Contains("id=\"terminalSelectAll\"", TerminalHtml);
        Assert.Contains("id=\"terminalClear\"", TerminalHtml);

        // Copy / cut serialize the current selection to the host clipboard.
        Assert.Contains("function copyTerminalSelection()", TerminalHtml);
        Assert.Contains("term.getSelection()", TerminalHtml);
        Assert.Contains("type: 'copyText'", TerminalHtml);
        Assert.Contains("term.clearSelection();", TerminalHtml);
        // Paste asks the host to read the clipboard and return it to the page-side line editor.
        Assert.Contains("type: 'requestPaste'", TerminalHtml);
        // Select all and clear act on the terminal surface directly.
        Assert.Contains("term.selectAll();", TerminalHtml);
        Assert.Contains("clearTerminalSurface(promptText);", TerminalHtml);
        Assert.DoesNotContain("sendTerminalInput('cls\\r', false);", TerminalHtml);

        Assert.Contains("case \"copyText\":", MainWindowTerminalSource);
        Assert.Contains("CopyTextToClipboard(", MainWindowTerminalSource);
        Assert.Contains("case \"requestPaste\":", MainWindowTerminalSource);
        Assert.Contains("PasteClipboardTextToTerminal()", MainWindowTerminalSource);
        Assert.Contains("Clipboard.SetContent(package);", MainWindowTerminalSource);
        Assert.Contains("Clipboard.GetContent()", MainWindowTerminalSource);
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

    [Fact]
    public void LinkHighlightManager_SkipsInvalidCharacterRegionsInsteadOfCrashingRender()
    {
        string method = ExtractMethod(LinkHighlightManagerSource, "FindAndComputeLinkPositions", window: 2600);
        AssertContainsInOrder(method,
            "this.links.Clear();",
            "string renderedText = textRenderer.RenderedText ?? string.Empty;",
            "renderedText.Length == 0 || textRenderer.DrawnTextLayout is null",
            "return;");
        Assert.Contains("match.Index < 0 || match.Index >= renderedText.Length || match.Length <= 0", method);
        Assert.Contains("int length = Math.Min(match.Length, renderedText.Length - match.Index);", method);
        Assert.Contains("try", method);
        Assert.Contains("textRenderer.DrawnTextLayout.GetCharacterRegions(match.Index, length);", method);
        Assert.Contains("catch (ArgumentException)", method);
        Assert.Contains("catch (COMException)", method);
        Assert.Contains("if (rects.Length == 0)", method);
        Assert.Contains("Length = length", method);
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
