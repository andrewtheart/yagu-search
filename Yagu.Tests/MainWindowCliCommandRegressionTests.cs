namespace Yagu.Tests;

public sealed class MainWindowCliCommandRegressionTests
{
    [Fact]
    public void AdvancedOptions_GenerateCliCommandButton_IsWiredToClosableOverlay()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"GenerateCliCommandButton\"", xaml);
        Assert.Contains("Click=\"OnGenerateCliCommandClick\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml);
        Assert.Contains("VerticalAlignment=\"Top\"", xaml);
        Assert.Contains("Padding=\"8,5\"", xaml);
        Assert.Contains("<FontIcon Glyph=\"&#xE756;\" FontSize=\"11\" />", xaml);
        Assert.Contains("<TextBlock Text=\"Generate CLI command\" FontSize=\"11\" />", xaml);
        Assert.Contains("Canvas.ZIndex=\"20\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandOverlay\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandBubble\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandText\"", xaml);
        Assert.Contains("x:Name=\"IncludeSavedSettingCliOptionsToggle\"", xaml);
        Assert.Contains("Header=\"Options already saved in settings\"", xaml);
        Assert.Contains("OnContent=\"Include\"", xaml);
        Assert.Contains("OffContent=\"Omit\"", xaml);
        Assert.Contains("Omit options that already match the current settings file", xaml);
        Assert.Contains("IsOn=\"False\"", xaml);
        Assert.DoesNotContain("IsOn=\"True\"", xaml);
        Assert.Contains("Toggled=\"OnGeneratedCliCommandSavedSettingOptionsToggled\"", xaml);
        Assert.DoesNotContain("settings-backed", xaml);
        Assert.Contains("FontFamily=\"Consolas\"", xaml);
        string commandTextSnippet = xaml.Substring(xaml.IndexOf("x:Name=\"GeneratedCliCommandText\"", StringComparison.Ordinal), 300);
        Assert.Contains("TextWrapping=\"Wrap\"", commandTextSnippet);
        Assert.DoesNotContain("TextWrapping=\"WrapWholeWords\"", commandTextSnippet);
        Assert.Contains("Click=\"OnCopyGeneratedCliCommandClick\"", xaml);
        Assert.Contains("x:Name=\"SendGeneratedCliCommandToTerminalButton\"", xaml);
        Assert.Contains("Click=\"OnSendGeneratedCliCommandToTerminalClick\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Send command to terminal\"", xaml);
        Assert.Contains("Click=\"OnCloseGeneratedCliCommandOverlayClick\"", xaml);
        int buttonIndex = xaml.IndexOf("x:Name=\"GenerateCliCommandButton\"", StringComparison.Ordinal);
        int controlStackIndex = xaml.IndexOf("<StackPanel Spacing=\"10\">", buttonIndex, StringComparison.Ordinal);
        int searchBehaviorIndex = xaml.IndexOf("<!-- Search behavior -->", StringComparison.Ordinal);

        Assert.True(buttonIndex < searchBehaviorIndex, "The Generate CLI command button should stay at the top-right of Advanced Options.");
        Assert.True(buttonIndex < controlStackIndex && controlStackIndex < searchBehaviorIndex, "The Generate CLI command button should float outside the Advanced Options control stack.");
        Assert.True(
            xaml.IndexOf("x:Name=\"GeneratedCliCommandOverlay\"", StringComparison.Ordinal) > xaml.IndexOf("<!-- Bottom status bar -->", StringComparison.Ordinal),
            "The generated CLI command should render in a root-level overlay, not inside Advanced Options.");
    }

    [Fact]
    public void CliCommandGenerator_EmitsExplicitSearchAndAdvancedOptionFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.CliCommand.cs"));
        string terminalSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
        string terminalHtml = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Assets", "terminal.html"));

        Assert.Contains("\"--directory\"", source);
        Assert.Contains("\"--pattern\"", source);
        Assert.Contains("\"--regex\"", source);
        Assert.Contains("\"--no-regex\"", source);
        Assert.Contains("\"--case-sensitive\"", source);
        Assert.Contains("\"--ignore-case\"", source);
        Assert.Contains("\"--exact-match\"", source);
        Assert.Contains("\"--no-exact-match\"", source);
        Assert.Contains("\"--search-mode\"", source);
        Assert.Contains("\"--include-regex\"", source);
        Assert.Contains("\"--exclude-regex\"", source);
        Assert.Contains("\"--skip-extensions\"", source);
        Assert.Contains("\"--search-archives\"", source);
        Assert.Contains("\"--archive-extensions\"", source);
        Assert.Contains("\"--max-depth\"", source);
        Assert.Contains("SearchOptions.ResolveContentSearchParallelism", source);
        Assert.Contains("BuildEffectiveSkipExtensionsForCli", source);
        Assert.Contains("QuoteCliValue", source);
        Assert.Contains("_includeGeneratedCliCommandSavedSettingOptions", source);
        Assert.Contains("OnGeneratedCliCommandSavedSettingOptionsToggled", source);
        Assert.Contains("BuildGeneratedCliCommand(bool includeSavedSettingOptions)", source);
        Assert.Contains("private bool _includeGeneratedCliCommandSavedSettingOptions;", source);
        Assert.Contains("BuildGeneratedCliCommand(includeSavedSettingOptions: false)", source);
        Assert.DoesNotContain("private bool _includeGeneratedCliCommandSavedSettingOptions = true;", source);
        Assert.DoesNotContain("BuildGeneratedCliCommand(includeSavedSettingOptions: true)", source);
        Assert.Contains("new SettingsService().Load()", source);
        Assert.Contains("ShouldIncludeSavedSettingOption", source);
        Assert.Contains("SplitSettingsPatternsForCli", source);
        Assert.Contains("AdminProtectedPathSegmentsEqual", source);
        Assert.Contains("CloseGeneratedCliCommandOverlay", source);
        Assert.Contains("Visibility.Collapsed", source);
        Assert.Contains("OnSendGeneratedCliCommandToTerminalClick", source);
        Assert.Contains("await SendTextToTerminalAsync(commandText.Text);", source);
        Assert.Contains("EnsureGeneratedCliCommandText(commandText);", source);
        Assert.Contains("private async Task SendTextToTerminalAsync(string text)", terminalSource);
        Assert.Contains("EnsureTerminalPaneExpandedAsync", terminalSource);
        Assert.Contains("await WaitForTerminalShellReadyAsync();", terminalSource);
        Assert.Contains("await ChangeTerminalDirectoryToYaguExecutableDirectoryAsync();", terminalSource);
        Assert.Contains("cd /d", terminalSource);
        Assert.Contains("Environment.ProcessPath", terminalSource);
        Assert.Contains("Path.GetDirectoryName(Environment.ProcessPath)", terminalSource);
        Assert.Contains("echoInput: false", terminalSource);
        Assert.Contains("TrimEnd('\\r', '\\n')", terminalSource);
        Assert.Contains("_terminalShellReadyCompletion", terminalSource);
        Assert.Contains("ContainsPrintableShellText", terminalSource);
        Assert.Contains("_terminalService?.WriteInput(commandText);", terminalSource);
        Assert.Contains("PostWebMessageAsJson(\"{\\\"type\\\":\\\"focus\\\"}\")", terminalSource);
        Assert.Contains("msg.type === 'focus'", terminalHtml);

        string ensureTerminalPane = terminalSource.Substring(
            terminalSource.IndexOf("private async Task EnsureTerminalPaneExpandedAsync()", StringComparison.Ordinal),
            500);
        Assert.Contains("SetTerminalPaneExpanded(true);", ensureTerminalPane);
        Assert.Contains("TerminalWebView.UpdateLayout();", ensureTerminalPane);
        Assert.DoesNotContain("if (!_terminalPaneExpanded)", ensureTerminalPane);
        Assert.DoesNotContain("SettingsBacked", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}