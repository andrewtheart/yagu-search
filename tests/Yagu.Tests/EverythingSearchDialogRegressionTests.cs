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
        // YaguDialog reads root.XamlRoot.RasterizationScale for DPI-aware content auto-sizing, which
        // is a legitimate use unrelated to the WinUI ContentDialog pattern (guarded above) — so the
        // absence of "XamlRoot" is intentionally no longer asserted.
    }

    [Fact]
    public void EverythingNotFoundPrompt_HasStandoutInstallRecommendation()
    {
        string startupChecks = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        // The not-found dialog uses a rich content builder (not a plain string) so it can render a
        // standout install recommendation.
        Assert.Contains("Content = BuildEverythingNotFoundContent(),", startupChecks);
        Assert.Contains("private static StackPanel BuildEverythingNotFoundContent()", startupChecks);

        // The recommendation stands out: bold, a distinct color, and a glyph.
        int start = startupChecks.IndexOf("private static StackPanel BuildEverythingNotFoundContent()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = startupChecks.Substring(start, System.Math.Min(1600, startupChecks.Length - start));
        Assert.Contains("SolidColorBrush(Microsoft.UI.Colors.DarkOrange)", body);
        Assert.Contains("FontWeight = Microsoft.UI.Text.FontWeights.Bold", body);
        Assert.Contains("Glyph = \"\\uE735\"", body); // filled star
        Assert.Contains("Very strongly recommended", body);
    }

    [Fact]
    public void TitleGlyph_UsesWidthConstrainedGridSoTitleWraps()
    {
        // Regression: the title glyph was originally placed in a horizontal StackPanel next to the
        // title TextBlock. A horizontal StackPanel measures its children with unbounded width, which
        // disables the title's TextWrapping and lets long titles overflow the dialog horizontally.
        // The glyph + title must live in a two-column Grid (Auto glyph + star title) so the title
        // column is width-constrained and the text wraps to fit the dialog.
        string dialog = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "YaguDialog.cs"));

        int glyphIndex = dialog.IndexOf("options.TitleGlyph is { Length: > 0 } glyph", StringComparison.Ordinal);
        Assert.True(glyphIndex >= 0, "Expected the title-glyph branch in YaguDialog.");

        // Within the glyph branch, the container must be a Grid with a star-sized title column, not a
        // horizontal StackPanel.
        int branchEnd = dialog.IndexOf("root.Children.Add(titleRow);", glyphIndex, StringComparison.Ordinal);
        Assert.True(branchEnd > glyphIndex, "Expected the title-glyph branch to add a titleRow.");
        string branch = dialog[glyphIndex..branchEnd];

        Assert.Contains("new Grid", branch);
        Assert.Contains("new GridLength(1, GridUnitType.Star)", branch);
        Assert.DoesNotContain("StackPanel { Orientation = Orientation.Horizontal", branch);

        // The title itself must keep wrapping enabled.
        Assert.Contains("TextWrapping = TextWrapping.WrapWholeWords", dialog);
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

    [Fact]
    public void EverythingInstall_PrefersBundledInstaller_ButAlwaysBehindConsent()
    {
        string root = FindRepoRoot();
        string startup = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
        string cli = File.ReadAllText(Path.Combine(root, "Yagu", "CliRunner.cs"));

        // Installing Everything ALWAYS requires consent — the bundled path never bypasses the prompt.
        // GUI: the "Install" dialog result; CLI: the [Y/n] answer.
        Assert.Contains("if (!installEverything) return;", startup);
        Assert.Contains("if (!IsYes(installAnswer)) return;", cli);

        // After consent, both flows prefer the pre-bundled offline installer over downloading.
        Assert.Contains("EverythingAssetPaths.BundledInstallerPath(", startup);
        Assert.Contains("EverythingAssetPaths.BundledInstallerPath(", cli);

        // The download branch is only taken when there is no bundle (the modal download helper and the
        // centralized voidtools URL are still used for the lite editions).
        Assert.Contains("if (!await DownloadEverythingInstallerAsync(url, tempPath))", startup);
        Assert.Contains("EverythingAssetPaths.DownloadUrl(", startup);
        Assert.Contains("EverythingAssetPaths.DownloadUrl(", cli);

        // Either source (bundle or download) is run only after passing the voidtools Authenticode check.
        Assert.Contains("AuthenticodeVerifier.IsTrustedPublisher(installerPath, EverythingAssetPaths.TrustedPublisher", startup);
        Assert.Contains("AuthenticodeVerifier.IsTrustedPublisher(installerPath, EverythingAssetPaths.TrustedPublisher", cli);

        // A signature failure deletes only a downloaded temp copy, never the bundled installer.
        Assert.Contains("if (!installerFromBundle) TryDeleteFile(installerPath);", startup);
        Assert.Contains("if (!installerFromBundle) { try { File.Delete(installerPath); } catch", cli);
    }

    [Fact]
    public void YaguDialog_AutoSizesHeightToContent_SoTextIsNotClipped()
    {
        string dialog = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "YaguDialog.cs"));

        // The dialog measures its content and resizes the window to fit, so long text (e.g. the
        // "Everything Search Not Found" body) is never clipped by the fixed Height — the dialog is
        // independent of the passed/backing window size.
        Assert.Contains("private void AutoSizeHeightToContent(", dialog);
        Assert.Contains("AutoSizeHeightToContent(root);", dialog);
        Assert.Contains("root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));", dialog);
        Assert.Contains("root.DesiredSize.Height", dialog);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner(", dialog);
        // Resizable dialogs keep the user's chosen size (auto-size only applies to fixed dialogs).
        Assert.Contains("if (_options.IsResizable)", dialog);

        // The measured height is the CLIENT height; the non-client frame (border + resize grip) must be
        // added back or it eats into the client and clips the last line. Query DPI-aware Win32 metrics
        // (AppWindow.Size vs ClientSize can lag after SetBorderAndTitleBar).
        Assert.Contains("private int NonClientFrameHeight()", dialog);
        Assert.Contains("GetSystemMetricsForDpi(33 /* SM_CYFRAME */", dialog);
        Assert.Contains("GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */", dialog);
        Assert.Contains("+ chromeHeight;", dialog);

        // The body is hosted in a ScrollViewer so content that still exceeds the available height
        // scrolls instead of being clipped — a hard guarantee independent of the height measurement.
        Assert.Contains("new ScrollViewer", dialog);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", dialog);

        // A second, settled re-fit pass catches wrapping/font-metric growth after the first measure.
        Assert.Contains("DispatcherQueuePriority.Low", dialog);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}