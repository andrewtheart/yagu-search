namespace Yagu.Tests;

/// <summary>
/// Pins that modal dialog surfaces honor the Yagu theme setting (Auto/Dark/Light) via the shared
/// <c>AppThemeService.ApplyThemedDialogSurface</c> helper, instead of reading
/// <c>ApplicationPageBackgroundThemeBrush</c> from <c>Application.Current.Resources</c> (which
/// resolves to the system/app theme and produces wrong-colored backgrounds when the Yagu theme
/// differs from the system theme).
/// </summary>
public sealed class ModalThemeSurfaceRegressionTests
{
    [Fact]
    public void AppThemeService_ExposesThemedDialogSurfaceHelper()
    {
        string src = ReadAppFile(Path.Combine("Services", "AppThemeService.cs"));

        Assert.Contains("public static void ApplyThemedDialogSurface(Grid surface, ElementTheme requestedTheme)", src);
        // Effective-theme background colors must match the title-bar surfaces.
        Assert.Contains("ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3)", src); // light surface
        Assert.Contains("ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)", src); // dark surface
        // Re-paint on load and on live theme changes.
        Assert.Contains("surface.Loaded +=", src);
        Assert.Contains("surface.ActualThemeChanged +=", src);
    }

    [Theory]
    [InlineData("UI/Windows/YaguDialog.cs")]
    [InlineData("UI/Windows/SemanticModelDownloadDialog.cs")]
    [InlineData("UI/Windows/SemanticModelQualificationDialog.cs")]
    [InlineData("UI/Windows/AdminProtectedPathsDialog.cs")]
    [InlineData("ResultStoreTempLocationWindow.cs")]
    public void DialogSurfaces_UseThemedHelper_NotAppResourceBackground(string relativePath)
    {
        string src = ReadAppFile(relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Contains("AppThemeService.ApplyThemedDialogSurface(", src);
        // The buggy app-resource background lookup must be gone from each surface.
        Assert.DoesNotContain("Background = ResourceBrush(\"ApplicationPageBackgroundThemeBrush\"", src);
    }

    [Fact]
    public void ResultStoreTempLocationWindow_DropsHardcodedDarkSurfaceAndWhiteText()
    {
        // This dialog previously hardcoded a dark surface and white text, so it never honored the
        // Light/Auto theme. The themed surface + inherited foreground must replace those.
        string src = ReadAppFile("ResultStoreTempLocationWindow.cs");

        Assert.DoesNotContain("Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20))", src);
        Assert.DoesNotContain("Foreground = new SolidColorBrush(Colors.White)", src);
    }

    [Fact]
    public void MainViewModel_KeepsStaticThemeIndexCurrent()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("partial void OnThemeModeIndexChanged(int value)", src);
        Assert.Contains("AppThemeService.CurrentThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(value)", src);
    }

    private static string ReadAppFile(string relativePath)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", relativePath));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
