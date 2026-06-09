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
        Assert.Contains("x:Name=\"ShowSettingsBackedCliFlagsToggle\"", xaml);
        Assert.Contains("Header=\"Show settings-backed flags\"", xaml);
        Assert.Contains("IsOn=\"True\"", xaml);
        Assert.Contains("Toggled=\"OnGeneratedCliCommandSettingsFlagsToggled\"", xaml);
        Assert.Contains("FontFamily=\"Consolas\"", xaml);
        string commandTextSnippet = xaml.Substring(xaml.IndexOf("x:Name=\"GeneratedCliCommandText\"", StringComparison.Ordinal), 300);
        Assert.Contains("TextWrapping=\"Wrap\"", commandTextSnippet);
        Assert.DoesNotContain("TextWrapping=\"WrapWholeWords\"", commandTextSnippet);
        Assert.Contains("Click=\"OnCopyGeneratedCliCommandClick\"", xaml);
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
        Assert.Contains("_showGeneratedCliCommandSettingsBackedFlags", source);
        Assert.Contains("OnGeneratedCliCommandSettingsFlagsToggled", source);
        Assert.Contains("BuildGeneratedCliCommand(bool includeSettingsBackedFlags)", source);
        Assert.Contains("new SettingsService().Load()", source);
        Assert.Contains("ShouldIncludeSettingsBackedFlag", source);
        Assert.Contains("SplitSettingsPatternsForCli", source);
        Assert.Contains("AdminProtectedPathSegmentsEqual", source);
        Assert.Contains("CloseGeneratedCliCommandOverlay", source);
        Assert.Contains("Visibility.Collapsed", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}