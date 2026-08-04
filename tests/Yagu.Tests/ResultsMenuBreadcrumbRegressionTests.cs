using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the Group / Filter menu breadcrumb header (shows the current
/// selection path at the top of each menu). The menus, their <c>Opening</c> handlers, and the
/// view-model breadcrumb strings live in WinUI/VM files that are not compiled into the test assembly,
/// so their contract is pinned here as source substrings per the repo's source-pin convention.
/// </summary>
public sealed class ResultsMenuBreadcrumbRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string PreviewCommandsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewCommands.cs"));
    private static readonly string MainViewModelSource = MainViewModelPartials.Text;

    [Fact]
    public void GroupMenu_HasBreadcrumbHeaderWiredToOpeningHandler()
    {
        Assert.Contains("<MenuFlyout Placement=\"BottomEdgeAlignedLeft\" Opening=\"OnGroupMenuOpening\">", MainWindowXaml);
        Assert.Contains("x:Name=\"GroupBreadcrumbItem\"", MainWindowXaml);
        Assert.Contains("x:Name=\"GroupBreadcrumbSeparator\"", MainWindowXaml);
        // The header is a non-interactive, initially-hidden row.
        Assert.Contains("IsEnabled=\"False\" Visibility=\"Collapsed\"", MainWindowXaml);
    }

    [Fact]
    public void FilterMenu_HasBreadcrumbHeaderWiredToOpeningHandler()
    {
        Assert.Contains("<MenuFlyout Placement=\"BottomEdgeAlignedLeft\" Opening=\"OnFilterMenuOpening\">", MainWindowXaml);
        Assert.Contains("x:Name=\"FilterBreadcrumbItem\"", MainWindowXaml);
        Assert.Contains("x:Name=\"FilterBreadcrumbSeparator\"", MainWindowXaml);
    }

    [Fact]
    public void OpeningHandlers_ApplyBreadcrumbTextAndVisibility()
    {
        Assert.Contains("private void OnGroupMenuOpening(object sender, object e)", PreviewCommandsSource);
        Assert.Contains("ApplyMenuBreadcrumb(GroupBreadcrumbItem, GroupBreadcrumbSeparator, ViewModel.HasGroupBreadcrumb, ViewModel.GroupBreadcrumb)", PreviewCommandsSource);
        Assert.Contains("private void OnFilterMenuOpening(object sender, object e)", PreviewCommandsSource);
        Assert.Contains("ApplyMenuBreadcrumb(FilterBreadcrumbItem, FilterBreadcrumbSeparator, ViewModel.HasFilterBreadcrumb, ViewModel.FilterBreadcrumb)", PreviewCommandsSource);
        Assert.Contains("item.Text = text;", PreviewCommandsSource);
        Assert.Contains("item.Visibility = visibility;", PreviewCommandsSource);
        Assert.Contains("separator.Visibility = visibility;", PreviewCommandsSource);
    }

    [Fact]
    public void ViewModel_ExposesGroupBreadcrumb()
    {
        Assert.Contains("public bool HasGroupBreadcrumb => GroupMode != GroupMode.None;", MainViewModelSource);
        Assert.Contains("$\"{GroupModeLabel}  \\u203A  {GroupSortDirectionLabel}\"", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_ExposesFilterBreadcrumb_CoveringDateAndExtension()
    {
        Assert.Contains("public bool HasFilterBreadcrumb => DateRangeFilter != DateRangeFilter.None || HasExtensionFilter;", MainViewModelSource);
        Assert.Contains("$\"By date  \\u203A  {DateRangeFilterLabel}\"", MainViewModelSource);
        Assert.Contains("$\"By extension  \\u203A  {ExtensionFilterLabel}\"", MainViewModelSource);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
