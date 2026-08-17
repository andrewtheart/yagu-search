namespace Yagu.Tests;

public sealed class DebugLogPanelRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowXaml = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string MainWindowDebugLog = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.DebugLog.cs"));
    private static readonly string MainWindowCodeBehind = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));
    private static readonly string SettingsService = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
    private static readonly string SettingsWindow = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string MainViewModel = MainViewModelPartials.Text;

    [Fact]
    public void Setting_DefaultsOnAndRoundTrips()
    {
        Assert.True(new Yagu.Services.AppSettings().ShowDebugPanel);
        string directory = Path.Combine(Path.GetTempPath(), "yagu-debug-panel-setting-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            var service = new Yagu.Services.SettingsService(path);
            service.Save(new Yagu.Services.AppSettings { ShowDebugPanel = false });
            Assert.False(service.Load().ShowDebugPanel);
            service.Save(new Yagu.Services.AppSettings { ShowDebugPanel = true });
            Assert.True(service.Load().ShowDebugPanel);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeveloperOption_IsDefaultOnPersistedAndControlsBottomRightButton()
    {
        Assert.Contains("public bool ShowDebugPanel { get; set; } = true;", SettingsService);
        Assert.Contains("[ObservableProperty] public partial bool ShowDebugPanel { get; set; } = true;", MainViewModel);
        Assert.Contains("ShowDebugPanel = _settings.ShowDebugPanel;", MainViewModel);
        Assert.Contains("_settings.ShowDebugPanel = ShowDebugPanel;", MainViewModel);
        Assert.Contains("public Microsoft.UI.Xaml.Visibility DebugPanelButtonVisibility =>", MainViewModel);

        Assert.Contains("Content = \"Show debug panel\"", SettingsWindow);
        Assert.Contains("_viewModel.ShowDebugPanel = true", SettingsWindow);
        Assert.Contains("_viewModel.ShowDebugPanel = false", SettingsWindow);

        Assert.Contains("x:Name=\"DebugLogButton\"", MainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.DebugPanelButtonVisibility, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("ToolTipService.ToolTip=\"Open live Yagu log\"", MainWindowXaml);
        Assert.Contains("Width=\"24\" Height=\"16\" MinWidth=\"0\" MinHeight=\"0\" Padding=\"0\"", MainWindowXaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\" Height=\"16\"", MainWindowXaml);
        Assert.DoesNotContain("Width=\"28\" Height=\"24\" MinWidth=\"0\" MinHeight=\"0\" Padding=\"0\"", MainWindowXaml);
    }

    [Fact]
    public void LiveTail_ExposesEveryPersistedDimensionAndIndependentLevels()
    {
        Assert.Contains("x:Name=\"DebugLogFlyout\"", MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"Category\" FontSize=\"11\" Opacity=\"0.65\" />", MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"Severity\" FontSize=\"11\" Opacity=\"0.65\" />", MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"Time\" FontSize=\"11\" Opacity=\"0.65\" />", MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"Message contains\" FontSize=\"11\" Opacity=\"0.65\" />", MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"View\" FontSize=\"11\" Opacity=\"0.65\" />", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogCategoryFilter\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogSeverityFilter\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogSinceFilter\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogTextFilter\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogFileLevel\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogConsoleLevel\"", MainWindowXaml);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Count(
            MainWindowXaml, @"DebugLog(?:CategoryFilter|SeverityFilter|SinceFilter|FileLevel|ConsoleLevel)""\r?\n\s+HorizontalAlignment=""Stretch"""));
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Count(
            MainWindowXaml, "Height=\"28\" MinHeight=\"0\" Padding=\"8,0\" FontSize=\"11\""));
        Assert.Contains("Height=\"28\" MinHeight=\"0\" Padding=\"8,2\" FontSize=\"11\"", MainWindowXaml);
        Assert.Contains("Height=\"28\" MinHeight=\"0\" Padding=\"0\" FontSize=\"11\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogExportButton\" Grid.Column=\"3\"", MainWindowXaml);
        Assert.Contains("ToolTipService.ToolTip=\"Export visible log entries\"", MainWindowXaml);
        Assert.Contains("Click=\"OnDebugLogExportVisible\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogRefreshButton\" Grid.Column=\"4\"", MainWindowXaml);
        Assert.Contains("ToolTipService.ToolTip=\"Refresh log now\"", MainWindowXaml);
        Assert.Contains("x:Name=\"DebugLogList\"", MainWindowXaml);
        Assert.Contains("x:DataType=\"services:LogTailEntry\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind TimestampText}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind Severity}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind Category}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind Message, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("ToolTipService.ToolTip=\"{x:Bind RawText, Mode=OneWay}\"", MainWindowXaml);

        Assert.Contains("new LogTailReader(LogService.Instance.LogFilePath)", MainWindowDebugLog);
        Assert.Contains("LogService.Instance.Flush();", MainWindowDebugLog);
        Assert.Contains("LogTailFilter.Apply(", MainWindowDebugLog);
        Assert.Contains("ViewModel.FileLogLevelIndex = DebugLogFileLevel.SelectedIndex - 1;", MainWindowDebugLog);
        Assert.Contains("ViewModel.ConsoleLogLevelIndex = DebugLogConsoleLevel.SelectedIndex - 1;", MainWindowDebugLog);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(MainWindowDebugLog, "await ViewModel.PersistSettingsAsync\\(\\);"));
        Assert.Contains("_debugLogTimer?.Stop();", MainWindowDebugLog);
        Assert.Contains("DisposeDebugLogPanel();", MainWindowCodeBehind);
    }

    [Fact]
    public void LiveTail_AllTextIsSelectable_AndRowsCopyTheirCompleteRawEntry()
    {
        Assert.Contains("<Style TargetType=\"TextBlock\">", MainWindowXaml);
        Assert.Contains("<Setter Property=\"IsTextSelectionEnabled\" Value=\"True\" />", MainWindowXaml);
        Assert.Contains("<Grid.ContextFlyout>", MainWindowXaml);
        Assert.Contains("Text=\"Copy log entry\"", MainWindowXaml);
        Assert.Contains("Tag=\"{x:Bind RawText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Click=\"OnCopyDebugLogEntry\"", MainWindowXaml);
        Assert.Contains("SetClipboardText(rawText, \"log entry\");", MainWindowDebugLog);
    }

    [Fact]
    public void LiveTail_ExportsExactlyTheVisibleFilteredEntries()
    {
        Assert.Contains("string[] visibleEntries = _debugLogVisibleEntries.Select", MainWindowDebugLog);
        Assert.Contains("entry => entry.RawText", MainWindowDebugLog);
        Assert.Contains("Win32FileDialog.Save(", MainWindowDebugLog);
        Assert.Contains("\"Export visible Yagu logs\"", MainWindowDebugLog);
        Assert.Contains("File.WriteAllTextAsync(path, text, new UTF8Encoding", MainWindowDebugLog);
        Assert.Contains("DebugLogExportButton.IsEnabled = filtered.Count > 0;", MainWindowDebugLog);
        Assert.DoesNotContain("_debugLogEntries.Select(static entry => entry.RawText)", MainWindowDebugLog);
    }

    [Fact]
    public void LiveTail_AlignsItsRightEdgeWithTheWindow()
    {
        Assert.Contains("x:Name=\"BottomStatusBar\" Grid.Row=\"6\" Padding=\"20,6,0,8\"", MainWindowXaml);
        int buttonStart = MainWindowXaml.IndexOf("x:Name=\"DebugLogButton\"", StringComparison.Ordinal);
        int flyoutStart = MainWindowXaml.IndexOf("<Button.Flyout>", buttonStart, StringComparison.Ordinal);
        Assert.True(buttonStart >= 0 && flyoutStart > buttonStart);
        string button = MainWindowXaml[buttonStart..flyoutStart];

        // The glyph alone moves left inside the same fixed button. Keeping the button bounds unchanged
        // preserves the flyout target and therefore the live-log overlay's right-edge alignment.
        Assert.Contains("Width=\"24\" Height=\"16\" MinWidth=\"0\" MinHeight=\"0\" Padding=\"0\"", button);
        Assert.Contains("<TranslateTransform X=\"-4\" />", button);
        Assert.DoesNotContain("Margin=", button);
        foreach (string key in new[]
                 {
                     "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
                 })
        {
            Assert.Contains($"<SolidColorBrush x:Key=\"{key}\" Color=\"Transparent\" />", button);
        }
        Assert.Contains("Placement=\"TopEdgeAlignedRight\"", MainWindowXaml[flyoutStart..]);
    }

    [Fact]
    public void LiveTail_ClosesFromABareGlyphWithNoButtonChrome()
    {
        int start = MainWindowXaml.IndexOf("x:Name=\"DebugLogCloseButton\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The live-log header must expose a close button.");
        int end = MainWindowXaml.IndexOf("</Button>", start, StringComparison.Ordinal);
        string close = MainWindowXaml[start..end];

        // Top-right of the header, next to the status text.
        Assert.Contains("Grid.Column=\"2\"", close);
        Assert.Contains("Glyph=\"&#xE711;\"", close);
        Assert.Contains("Click=\"OnDebugLogCloseClicked\"", close);
        Assert.Contains("AutomationProperties.Name=\"Close the live log\"", close);

        // "Bare x": no border and every background/border visual state stays transparent, so no square
        // appears at rest, on hover, or while pressed.
        Assert.Contains("BorderThickness=\"0\"", close);
        foreach (string key in new[]
                 {
                     "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
                 })
        {
            Assert.Contains($"<SolidColorBrush x:Key=\"{key}\" Color=\"Transparent\" />", close);
        }

        Assert.Contains("private void OnDebugLogCloseClicked(object sender, RoutedEventArgs e) => DebugLogFlyout.Hide();", MainWindowDebugLog);
    }

    [Fact]
    public void LiveTail_UsesStableThemedSurfaceAndDoesNotRecreateTheListEachTick()
    {
        int start = MainWindowXaml.IndexOf("x:Name=\"DebugLogFlyout\"", StringComparison.Ordinal);
        int end = MainWindowXaml.IndexOf("<!-- Index-status hover overlay", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string flyout = MainWindowXaml[start..end];

        Assert.Contains("x:Name=\"DebugLogSurface\"", flyout);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" />", flyout);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(
            flyout, "Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\""));
        Assert.Contains("ObservableCollection<LogTailEntry> _debugLogVisibleEntries", MainWindowDebugLog);
        Assert.Contains("DebugLogList.ItemsSource = _debugLogVisibleEntries;", MainWindowDebugLog);
        Assert.Contains("SynchronizeDebugLogVisibleEntries(filtered)", MainWindowDebugLog);
        Assert.DoesNotContain("DebugLogList.ItemsSource = filtered", MainWindowDebugLog);
        Assert.Contains("visibleEntriesChanged && DebugLogFollowTail.IsChecked == true", MainWindowDebugLog);
    }

    [Fact]
    public void LiveTail_UsesConventionalThemeAwareSeverityColors()
    {
        Assert.Contains("<local:DebugLogSeverityBrushConverter x:Key=\"DebugLogSeverityBrushConverter\" />", MainWindowXaml);
        Assert.Contains("Foreground=\"{Binding Level, Converter={StaticResource DebugLogSeverityBrushConverter}}\"", MainWindowXaml);
        Assert.Contains("FontFamily=\"Consolas\" FontSize=\"11\" FontWeight=\"SemiBold\"", MainWindowXaml);

        Assert.Contains("LogLevel.Critical => \"SystemFillColorCriticalBrush\"", MainWindowDebugLog);
        Assert.Contains("LogLevel.Warning => \"SystemFillColorCautionBrush\"", MainWindowDebugLog);
        Assert.Contains("LogLevel.Info => \"AccentTextFillColorPrimaryBrush\"", MainWindowDebugLog);
        Assert.Contains("_ => \"TextFillColorSecondaryBrush\"", MainWindowDebugLog);
        Assert.Contains("Application.Current.Resources.TryGetValue(resourceKey", MainWindowDebugLog);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yagu.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}