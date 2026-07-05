namespace Yagu.Tests;

public sealed class TitleBarThemeRegressionTests
{
    [Fact]
    public void LightTheme_UsesBlackForegroundForNativeAndCustomTopBarIcons()
    {
        string root = FindRepoRoot();
        string themeService = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Services", "AppThemeService.cs"));
        string titleBarSource = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.TitleBar.cs"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        Assert.Contains("var lightForeground = Colors.Black;", themeService);
        Assert.Contains("var lightInactiveForeground = Colors.Black;", themeService);
        Assert.Contains("titleBar.ButtonForegroundColor = lightForeground;", themeService);
        Assert.Contains("titleBar.ButtonHoverForegroundColor = lightForeground;", themeService);
        Assert.Contains("titleBar.ButtonPressedForegroundColor = lightForeground;", themeService);

        Assert.Contains("actualTheme == ElementTheme.Light ? Colors.Black : Colors.White", titleBarSource);
        Assert.Contains("case Control control:", titleBarSource);
        Assert.Contains("case TextBlock textBlock:", titleBarSource);
        Assert.Contains("case FontIcon icon:", titleBarSource);
        Assert.Contains("case IconElement iconElement:", titleBarSource);

        string actions = ExtractWindow(mainWindowXaml, "x:Name=\"TitleBarActions\"", 1700);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", actions);
        Assert.DoesNotContain("Foreground=\"White\"", actions);
    }

    private static string ExtractWindow(string source, string marker, int length)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find marker: {marker}");
        return source.Substring(index, Math.Min(length, source.Length - index));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}