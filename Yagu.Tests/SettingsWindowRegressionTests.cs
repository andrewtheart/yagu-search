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
        Path.Combine(RepoRoot, "Yagu", "MainWindow.xaml.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "SettingsWindow.xaml.cs"));
    private static readonly string SettingsWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "SettingsWindow.xaml"));
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
    public void SettingsWindow_SetsUpCustomTitleBar()
    {
        Assert.Contains("ExtendsContentIntoTitleBar = true;", SettingsWindowSource);
        Assert.Contains("SetTitleBar(AppTitleBar);", SettingsWindowSource);
    }

    [Fact]
    public void SettingsWindow_SavePersistsAndCloses()
    {
        string saveMethod = ExtractMethod(SettingsWindowSource, "OnSaveClick");
        Assert.Contains("PersistSettingsAsync()", saveMethod);
        Assert.Contains("Close()", saveMethod);
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
    public void SettingsWindow_HotkeySection_QueriesAvailableKeys()
    {
        Assert.Contains("GetAvailableCtrlShiftLetterKeys(_mainHwnd)", SettingsWindowSource);
        Assert.Contains("ChooseAvailableKey(", SettingsWindowSource);
    }

    // ── MainWindow settings integration ──

    [Fact]
    public void MainWindow_OnOpenSettings_CreatesSettingsWindow()
    {
        string method = ExtractMethod(MainWindowSource, "OnOpenSettings", window: 800);
        Assert.Contains("new SettingsWindow(", method);
        Assert.Contains("_settingsWindow.Activate()", method);
        // Settings are now in a standalone window, not a ContentDialog
        Assert.DoesNotContain("new ContentDialog", method);
    }

    [Fact]
    public void MainWindow_OnOpenSettings_ReusesExistingWindow()
    {
        string method = ExtractMethod(MainWindowSource, "OnOpenSettings");
        Assert.Contains("if (_settingsWindow is not null)", method);
        // try-catch around Activate to handle the case where the window was already closed
        Assert.Contains("try { _settingsWindow.Activate(); return; }", method);
        Assert.Contains("catch { _settingsWindow = null; }", method);
    }

    [Fact]
    public void MainWindow_OnOpenSettings_ClearsReferenceOnClose()
    {
        string method = ExtractMethod(MainWindowSource, "OnOpenSettings");
        Assert.Contains("_settingsWindow.Closed += ", method);
        Assert.Contains("_settingsWindow = null", method);
    }

    [Fact]
    public void MainWindow_OnWindowActivated_SuppressesTrayHideForSettings()
    {
        string method = ExtractMethod(MainWindowSource, "OnWindowActivated");
        AssertContainsInOrder(method,
            "WindowActivationState.Deactivated",
            "_settingsWindow is not null",
            "return;",
            "HideToTray()");
    }

    [Fact]
    public void MainWindow_HasSettingsWindowField()
    {
        Assert.Contains("private SettingsWindow? _settingsWindow;", MainWindowSource);
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
