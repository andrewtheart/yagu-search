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
        // SetBorderAndTitleBar alone does not reliably remove the OS title bar; the window must also
        // extend content into the title bar (matching MainWindow/SettingsWindow/ResultStoreTempLocationWindow).
        Assert.Contains("if (!options.ShowTitleBar)", dialog);
        Assert.Contains("ExtendsContentIntoTitleBar = true;", dialog);
        // Startup-time dialogs (the Everything prompts) can have SetBorderAndTitleBar silently fail to
        // apply before the window is realized; YaguDialog must re-apply the presenter config after
        // Activate() so the caption is reliably removed regardless of when the dialog is shown.
        Assert.Contains("DispatcherQueue.TryEnqueue(() => TryConfigurePresenter(_appWindow, _options.IsResizable, _options.ShowTitleBar));", dialog);
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

    [Fact]
    public void EverythingNotRunningPrompts_AreTitleless()
    {
        string root = FindRepoRoot();
        string startupChecks = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        const string titleMarker = "Title = \"Everything Search Not Running\"";
        int count = 0;
        int index = startupChecks.IndexOf(titleMarker, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            int blockEnd = startupChecks.IndexOf("}) == YaguDialogResult.Primary", index, StringComparison.Ordinal);
            Assert.True(blockEnd > index, "Could not find end of 'Everything Search Not Running' dialog options block.");
            string block = startupChecks.Substring(index, blockEnd - index);
            Assert.Contains("ShowTitleBar = false", block);
            index = startupChecks.IndexOf(titleMarker, index + titleMarker.Length, StringComparison.Ordinal);
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void EverythingSearchReadyDialog_IsTitleless()
    {
        string root = FindRepoRoot();
        string startupChecks = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        const string titleMarker = "Title = \"Everything Search Ready\"";
        int index = startupChecks.IndexOf(titleMarker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Could not find 'Everything Search Ready' dialog options.");
        int blockEnd = startupChecks.IndexOf("});", index, StringComparison.Ordinal);
        Assert.True(blockEnd > index, "Could not find end of 'Everything Search Ready' dialog options block.");
        string block = startupChecks.Substring(index, blockEnd - index);
        Assert.Contains("ShowTitleBar = false", block);
    }

    [Fact]
    public void EverythingNotRunningPrompt_OffersDontShowAgainBackedByRestorableSetting()
    {
        string root = FindRepoRoot();
        string startupChecks = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
        string settingsService = File.ReadAllText(Path.Combine(root, "Yagu", "Services", "SettingsService.cs"));
        string settingsWindow = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        // The not-running prompt body carries a "Don't show this again" checkbox.
        Assert.Contains("Content = \"Don't show this again\"", startupChecks);
        // Checking it persists a suppression setting, and that setting short-circuits the prompt.
        Assert.Contains("ViewModel.SuppressEverythingNotRunningPrompt = true;", startupChecks);
        Assert.Contains("if (ViewModel.SuppressEverythingNotRunningPrompt)", startupChecks);
        // The setting is persisted.
        Assert.Contains("public bool SuppressEverythingNotRunningPrompt", settingsService);
        // Developer Options exposes a restore (re-enable) button.
        Assert.Contains("if (_viewModel.SuppressEverythingNotRunningPrompt)", settingsWindow);
        Assert.Contains("_viewModel.SuppressEverythingNotRunningPrompt = false;", settingsWindow);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}