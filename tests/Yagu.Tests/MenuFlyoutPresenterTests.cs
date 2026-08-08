namespace Yagu.Tests;

public sealed class MenuFlyoutPresenterTests
{
    private static readonly string AppXaml = File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "Yagu", "App.xaml"));

    [Fact]
    public void AppResources_RemoveOuterBorderFromEveryMenuFlyoutPresenter()
    {
        Assert.Contains(
            "<Thickness x:Key=\"MenuFlyoutPresenterBorderThemeThickness\">0</Thickness>",
            AppXaml);
        Assert.Contains(
            "<SolidColorBrush x:Key=\"MenuFlyoutPresenterBorderBrush\" Color=\"Transparent\" />",
            AppXaml);
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Yagu.slnx")))
            directory = Directory.GetParent(directory)?.FullName;
        return directory ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}