namespace Yagu.Tests;

public sealed class EverythingSearchDialogRegressionTests
{
    [Fact]
    public void EverythingNotFoundPrompt_UsesSharedCustomDialog()
    {
        string root = FindRepoRoot();
        string startupChecks = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
        string dialog = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "YaguDialog.cs"));

        Assert.Contains("Title = \"Everything Search Not Found\"", startupChecks);
        Assert.Contains("YaguDialog.ShowAsync(", startupChecks);
        Assert.Contains("PrimaryButtonText = \"Install\"", startupChecks);
        Assert.Contains("ShowTitleBar = false", startupChecks);
        Assert.DoesNotContain("EverythingSearchNotFoundDialog", startupChecks);

        Assert.Contains("internal sealed class YaguDialog : Window", dialog);
        Assert.Contains("presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);", dialog);
        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);", dialog);
        Assert.Contains("EnableWindow(_ownerHwnd, false);", dialog);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, options.Width, options.Height);", dialog);
        Assert.Contains("public static bool HasOpenOwnedWindow(IntPtr ownerHwnd)", dialog);
        Assert.DoesNotContain("ContentDialog", dialog);
        Assert.DoesNotContain("XamlRoot", dialog);
    }

    [Fact]
    public void AppCode_DoesNotUseWinUiContentDialog()
    {
        string root = FindRepoRoot();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "Yagu"), "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path);
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("ContentDialog", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ContentDialogResult", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ContentDialogButton", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}