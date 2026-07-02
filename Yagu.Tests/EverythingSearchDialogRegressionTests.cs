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

    [Fact]
    public void EverythingInstallerDownload_ShowsProgressModalAndOfflineFailureModal()
    {
        string root = FindRepoRoot();
        string startupChecks = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        // The installer download is gated by a helper that shows a modal instead of the old silent
        // one-shot GetByteArrayAsync download that only updated the status bar.
        Assert.Contains("if (!await DownloadEverythingInstallerAsync(url, tempPath))", startupChecks);
        Assert.DoesNotContain("GetByteArrayAsync", startupChecks);

        // A modal progress dialog with a real progress bar is shown while downloading, and it is
        // cancellable and titleless.
        const string progressTitle = "Title = \"Getting Everything Search\"";
        int progressIndex = startupChecks.IndexOf(progressTitle, StringComparison.Ordinal);
        Assert.True(progressIndex >= 0, "Could not find the Everything download progress dialog.");
        int progressEnd = startupChecks.IndexOf("Width = 480", progressIndex, StringComparison.Ordinal);
        Assert.True(progressEnd > progressIndex, "Could not find end of the progress dialog options block.");
        string progressBlock = startupChecks.Substring(progressIndex, progressEnd - progressIndex);
        Assert.Contains("ShowTitleBar = false", progressBlock);
        Assert.Contains("CloseButtonText = \"Cancel\"", progressBlock);
        Assert.Contains("new ProgressBar", startupChecks);

        // The download streams so it can report progress (rather than buffering the whole file).
        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", startupChecks);

        // A clear failure modal is shown when the download fails (e.g. no internet), and it is titleless.
        const string failTitle = "Title = \"Couldn't download Everything Search\"";
        int failIndex = startupChecks.IndexOf(failTitle, StringComparison.Ordinal);
        Assert.True(failIndex >= 0, "Could not find the Everything download failure modal.");
        int failEnd = startupChecks.IndexOf("Width = 520", failIndex, StringComparison.Ordinal);
        Assert.True(failEnd > failIndex, "Could not find end of the failure modal options block.");
        string failBlock = startupChecks.Substring(failIndex, failEnd - failIndex);
        Assert.Contains("ShowTitleBar = false", failBlock);
        // The failure message distinguishes an unreachable host (offline) from a timeout.
        Assert.Contains("check your internet connection", startupChecks);
        Assert.Contains("HttpRequestException", startupChecks);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}