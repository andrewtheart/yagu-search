namespace Yagu.Tests;

using System.Text.RegularExpressions;

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

    [Fact]
    public void AdminBanner_UsesThemeAwareCautionBrushesInsteadOfHardCodedDarkBackground()
    {
        string root = FindRepoRoot();
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        string appXaml = File.ReadAllText(Path.Combine(root, "src", "Yagu", "App.xaml"));

        // The old hard-coded dark background left the inherited black text invisible in light theme.
        Assert.DoesNotContain("Background=\"#332000\"", mainWindowXaml);

        string banner = ExtractWindow(mainWindowXaml, "x:Name=\"AdminBanner\"", 2200);
        Assert.Contains("Background=\"{ThemeResource YaguAdminBannerBackgroundBrush}\"", banner);
        Assert.Contains("Foreground=\"{ThemeResource YaguAdminBannerForegroundBrush}\"", banner);
        Assert.Contains("Foreground=\"{ThemeResource YaguAdminBannerIconBrush}\"", banner);

        foreach (string key in new[]
        {
            "YaguAdminBannerBackgroundBrush",
            "YaguAdminBannerBorderBrush",
            "YaguAdminBannerForegroundBrush",
            "YaguAdminBannerIconBrush",
        })
        {
            // Default (dark), Light and HighContrast theme dictionaries must each define the key.
            Assert.Equal(3, Regex.Matches(appXaml, $"x:Key=\"{key}\"").Count);
        }

        // Light theme must stay yellow with dark, legible text.
        string light = ExtractWindow(appXaml, "<ResourceDictionary x:Key=\"Light\">", 6000);
        Assert.Contains("<SolidColorBrush x:Key=\"YaguAdminBannerBackgroundBrush\" Color=\"#FFF1C1\" />", light);
        Assert.Contains("<SolidColorBrush x:Key=\"YaguAdminBannerForegroundBrush\" Color=\"#2B2100\" />", light);
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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}